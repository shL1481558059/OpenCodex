using System.Text;
using System.Text.Json;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Errors;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ChatMessagesStreamingCompatibilityTests
{
    [Theory]
    [InlineData("text(1+1);")]
    [InlineData("{\"input\":\"literal\"}")]
    [InlineData("text(\"雪😀\\path\");\r\ntext('next');")]
    [InlineData("{}")]
    [InlineData("")]
    public async Task MessagesToResponses_MappedCustomTool_DecodesFragmentedEnvelopeExactlyOnce(string input)
    {
        var blocks = new List<string> { MessagesToolStart(0, "call_exec", "exec") };
        foreach (var fragment in JsonSerializer.Serialize(new { input }).Select(c => c.ToString()))
        {
            blocks.Add(MessagesInputDelta(0, fragment));
        }
        blocks.Add(MessagesFinish("tool_use"));
        blocks.Add(SseData(new { type = "message_stop" }));

        var parsed = ParseEvents(await CollectAsync(SseStreamConverter.MessagesToResponsesEvents(
            SseLines(blocks.ToArray()), "claude", CustomToolStreamResult(), CancellationToken.None)));
        var delta = Assert.Single(parsed, e => e.EventName == "response.custom_tool_call_input.delta");
        Assert.Equal(input, delta.Payload["delta"]);
        var done = Assert.Single(parsed, e => e.EventName == "response.custom_tool_call_input.done");
        Assert.Equal(input, done.Payload["input"]);
        var itemDone = Assert.Single(parsed, e => e.EventName == "response.output_item.done");
        Assert.Equal(input, Assert.IsType<Dictionary<string, object?>>(itemDone.Payload["item"])["input"]);
        var completed = Assert.Single(parsed, e => e.EventName == "response.completed");
        var response = Assert.IsType<Dictionary<string, object?>>(completed.Payload["response"]);
        var output = Assert.IsType<List<object?>>(response["output"]);
        Assert.Equal(input, Assert.IsType<Dictionary<string, object?>>(Assert.Single(output))["input"]);
    }

    [Fact]
    public async Task MessagesToResponses_MappedCustomTool_InitialInputWithoutDeltasIsDecoded()
    {
        const string input = "{\"input\":\"literal\"}";
        var lines = SseLines(
            SseData(new { type = "content_block_start", index = 0,
                content_block = new { type = "tool_use", id = "call_exec", name = "exec", input = new { input } } }),
            MessagesFinish("tool_use"), SseData(new { type = "message_stop" }));
        var parsed = ParseEvents(await CollectAsync(SseStreamConverter.MessagesToResponsesEvents(
            lines, "claude", CustomToolStreamResult(), CancellationToken.None)));
        Assert.Equal(input, Assert.Single(parsed, e => e.EventName == "response.custom_tool_call_input.delta").Payload["delta"]);
        Assert.Equal(input, Assert.Single(parsed, e => e.EventName == "response.custom_tool_call_input.done").Payload["input"]);
    }

    [Fact]
    public async Task MessagesToResponses_MappedCustomTools_InterleavedCallsKeepSeparateInputs()
    {
        var streamResult = CustomToolStreamResult();
        streamResult.ToolCallMappings = new Dictionary<string, ResponsesToolCallMapping>(streamResult.ToolCallMappings!)
        {
            ["search"] = new() { ChatName = "search", ResponsesName = "search", NativeType = "function" }
        };
        var first = JsonSerializer.Serialize(new { input = "text('first');" });
        var second = JsonSerializer.Serialize(new { input = "{\"input\":\"second\"}" });
        var lines = SseLines(
            MessagesToolStart(0, "first", "exec"), MessagesInputDelta(0, first[..8]),
            MessagesToolStart(1, "second", "exec"), MessagesInputDelta(1, second[..8]),
            MessagesToolStart(2, "search", "search"), MessagesInputDelta(2, "{\"q\":1}"),
            MessagesInputDelta(1, second[8..]), MessagesInputDelta(0, first[8..]),
            MessagesFinish("tool_use"), SseData(new { type = "message_stop" }));
        var parsed = ParseEvents(await CollectAsync(SseStreamConverter.MessagesToResponsesEvents(
            lines, "claude", streamResult, CancellationToken.None)));
        var items = parsed.Where(e => e.EventName == "response.output_item.done")
            .Select(e => Assert.IsType<Dictionary<string, object?>>(e.Payload["item"]))
            .ToDictionary(i => i["call_id"]!.ToString()!);
        Assert.Equal("text('first');", items["first"]["input"]);
        Assert.Equal("{\"input\":\"second\"}", items["second"]["input"]);
        Assert.Equal("{\"q\":1}", items["search"]["arguments"]);
        Assert.Equal(2, parsed.Count(e => e.EventName == "response.custom_tool_call_input.delta"));
        Assert.Single(parsed, e => e.EventName == "response.function_call_arguments.delta");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"code\":\"text(1);\"}")]
    [InlineData("{\"input\":null}")]
    [InlineData("{\"input\":123}")]
    [InlineData("{\"input\":\"x\",\"extra\":true}")]
    [InlineData("{\"input\":\"x\",\"input\":\"y\"}")]
    [InlineData("{\"input\":\"unfinished")]
    public async Task MessagesToResponses_MappedCustomTool_InvalidEnvelopeNeverCompletes(string arguments)
    {
        var lines = SseLines(MessagesToolStart(0, "call_exec", "exec"), MessagesInputDelta(0, arguments),
            MessagesFinish("tool_use"), SseData(new { type = "message_stop" }));
        var emitted = new List<string>();
        await Assert.ThrowsAsync<UpstreamException>(async () =>
        {
            await foreach (var e in SseStreamConverter.MessagesToResponsesEvents(
                lines, "claude", CustomToolStreamResult(), CancellationToken.None))
            {
                emitted.Add(e);
            }
        });
        Assert.DoesNotContain(ParseEvents(emitted), e => e.EventName is
            "response.custom_tool_call_input.delta" or "response.custom_tool_call_input.done"
            or "response.output_item.done" or "response.completed");
    }

    [Theory]
    [InlineData("max_tokens", true)]
    [InlineData("refusal", true)]
    [InlineData(null, false)]
    public async Task MessagesToResponses_MappedCustomTool_InterruptedStreamNeverCompletes(string? stopReason, bool stopped)
    {
        var blocks = new List<string>
        {
            MessagesToolStart(0, "call_exec", "exec"), MessagesInputDelta(0, "{\"input\":\"text(1);\"}")
        };
        if (stopReason is not null) blocks.Add(MessagesFinish(stopReason));
        if (stopped) blocks.Add(SseData(new { type = "message_stop" }));
        var emitted = new List<string>();
        await Assert.ThrowsAsync<UpstreamException>(async () =>
        {
            await foreach (var e in SseStreamConverter.MessagesToResponsesEvents(
                SseLines(blocks.ToArray()), "claude", CustomToolStreamResult(), CancellationToken.None))
            {
                emitted.Add(e);
            }
        });
        Assert.DoesNotContain(ParseEvents(emitted), e => e.EventName is
            "response.custom_tool_call_input.delta" or "response.custom_tool_call_input.done"
            or "response.output_item.done" or "response.completed");
    }

    private static ConvertedStreamResult CustomToolStreamResult() => new()
    {
        ToolCallMappings = new Dictionary<string, ResponsesToolCallMapping>
        {
            ["exec"] = new() { ChatName = "exec", ResponsesName = "exec", NativeType = "custom" }
        }
    };

    private static string MessagesToolStart(int index, string id, string name) => SseData(new
    {
        type = "content_block_start", index,
        content_block = new { type = "tool_use", id, name, input = new Dictionary<string, object?>() }
    });

    private static string MessagesInputDelta(int index, string fragment) => SseData(new
    {
        type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = fragment }
    });

    private static string MessagesFinish(string stopReason) => SseData(new
    {
        type = "message_delta", delta = new { stop_reason = stopReason }, usage = new { output_tokens = 1 }
    });

    [Fact]
    public async Task ChatToMessages_InterleavedParallelTools_NeverDeltaAfterBlockStop()
    {
        var lines = SseLines(
            ChatToolChunk(0, "call_0", "first", "{\"value\":"),
            ChatToolChunk(1, "call_1", "second", "{\"value\":"),
            ChatToolChunk(0, null, null, "1}"),
            ChatToolChunk(1, null, null, "2}"),
            ChatFinishChunk("tool_calls"),
            "data: [DONE]\n\n");

        var events = await CollectAsync(SseStreamConverter.ChatToMessagesEvents(
            lines,
            "gpt-5",
            new ConvertedStreamResult(),
            CancellationToken.None));

        var openBlocks = new HashSet<int>();
        var closedBlocks = new HashSet<int>();
        var argumentsByBlock = new Dictionary<int, StringBuilder>();

        foreach (var (eventName, payload) in ParseEvents(events))
        {
            var index = payload.TryGetValue("index", out var rawIndex) ? Convert.ToInt32(rawIndex) : -1;
            switch (eventName)
            {
                case "content_block_start" when IsToolUse(payload):
                    Assert.DoesNotContain(index, openBlocks);
                    Assert.DoesNotContain(index, closedBlocks);
                    openBlocks.Add(index);
                    argumentsByBlock[index] = new StringBuilder();
                    break;
                case "content_block_delta" when IsInputJsonDelta(payload):
                    Assert.Contains(index, openBlocks);
                    Assert.DoesNotContain(index, closedBlocks);
                    argumentsByBlock[index].Append(Delta(payload)["partial_json"]?.ToString());
                    break;
                case "content_block_stop":
                    Assert.Contains(index, openBlocks);
                    openBlocks.Remove(index);
                    closedBlocks.Add(index);
                    break;
            }
        }

        Assert.Empty(openBlocks);
        Assert.Equal(2, argumentsByBlock.Count);
        Assert.Equal(["{\"value\":1}", "{\"value\":2}"],
            argumentsByBlock.OrderBy(pair => pair.Key).Select(pair => pair.Value.ToString()).ToArray());
    }

    [Fact]
    public async Task MessagesToChat_IncludeUsageFalse_DoesNotEmitUsageChunk()
    {
        var lines = SseLines(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude\",\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n\n",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");

        var events = await CollectAsync(SseStreamConverter.MessagesToChatEvents(
            lines,
            "claude",
            new ConvertedStreamResult(),
            SkipToolNames: null,
            IncludeUsage: false,
            CancellationToken.None));

        Assert.Contains("[DONE]", events[^1]);
        Assert.DoesNotContain(ParseEvents(events), item => item.Payload.ContainsKey("usage"));
    }

    [Fact]
    public async Task ChatToMessages_Error_DoesNotEmitNormalCompletion()
    {
        var lines = SseLines(
            "data: {\"error\":{\"type\":\"server_error\",\"message\":\"boom\"}}\n\n");

        var events = await CollectAsync(SseStreamConverter.ChatToMessagesEvents(
            lines,
            "gpt-5",
            new ConvertedStreamResult(),
            CancellationToken.None));
        var parsed = ParseEvents(events);

        Assert.Contains(parsed, item => item.EventName == "error" && item.Payload.ContainsKey("error"));
        Assert.DoesNotContain(parsed, item => item.EventName is "message_delta" or "message_stop");
    }

    [Fact]
    public async Task MessagesToChat_Error_DoesNotEmitFinishOrDone()
    {
        var lines = SseLines(
            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"boom\"}}\n\n");

        var events = await CollectAsync(SseStreamConverter.MessagesToChatEvents(
            lines,
            "claude",
            new ConvertedStreamResult(),
            CancellationToken.None));
        var parsed = ParseEvents(events);

        Assert.Single(parsed);
        Assert.True(parsed[0].Payload.ContainsKey("error"));
        Assert.DoesNotContain(events, item => item.Contains("finish_reason", StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.Contains("[DONE]", StringComparison.Ordinal));
    }

    private static bool IsToolUse(Dictionary<string, object?> payload)
        => payload.TryGetValue("content_block", out var block)
           && block is Dictionary<string, object?> contentBlock
           && contentBlock.TryGetValue("type", out var type)
           && type?.ToString() == "tool_use";

    private static bool IsInputJsonDelta(Dictionary<string, object?> payload)
        => Delta(payload).TryGetValue("type", out var type)
           && type?.ToString() == "input_json_delta";

    private static Dictionary<string, object?> Delta(Dictionary<string, object?> payload)
        => payload.TryGetValue("delta", out var delta) && delta is Dictionary<string, object?> value
            ? value
            : [];

    private static string ChatToolChunk(int index, string? id, string? name, string arguments)
    {
        var toolCall = new Dictionary<string, object?>
        {
            ["index"] = index,
            ["function"] = new Dictionary<string, object?> { ["arguments"] = arguments }
        };
        if (id is not null) toolCall["id"] = id;
        if (name is not null)
        {
            ((Dictionary<string, object?>)toolCall["function"]!)["name"] = name;
            toolCall["type"] = "function";
        }

        return SseData(new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_1",
            ["model"] = "gpt-5",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["tool_calls"] = new List<object?> { toolCall }
                    },
                    ["finish_reason"] = null
                }
            }
        });
    }

    private static string ChatFinishChunk(string finishReason) => SseData(new Dictionary<string, object?>
    {
        ["id"] = "chatcmpl_1",
        ["model"] = "gpt-5",
        ["choices"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["index"] = 0,
                ["delta"] = new Dictionary<string, object?>(),
                ["finish_reason"] = finishReason
            }
        }
    });

    private static string SseData(object payload)
        => $"data: {JsonSerializer.Serialize(payload)}\n\n";

    private static async IAsyncEnumerable<string> SseLines(params string[] blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var line in block.Split('\n'))
            {
                yield return line;
            }
        }

        await Task.CompletedTask;
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var result = new List<string>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }

    private static List<(string EventName, Dictionary<string, object?> Payload)> ParseEvents(IEnumerable<string> events)
    {
        var result = new List<(string, Dictionary<string, object?>)>();
        foreach (var item in events)
        {
            var eventName = item.Split('\n').FirstOrDefault(line => line.StartsWith("event: ", StringComparison.Ordinal))?[7..]
                ?? "message";
            var data = item.Split('\n').FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal))?[6..];
            if (data is null || data == "[DONE]") continue;
            using var document = JsonDocument.Parse(data);
            result.Add((eventName, (Dictionary<string, object?>)FromJson(document.RootElement)!));
        }

        return result;
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => FromJson(property.Value),
            StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var value) ? value : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}

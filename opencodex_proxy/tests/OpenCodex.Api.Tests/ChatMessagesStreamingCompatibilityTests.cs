using System.Text;
using System.Text.Json;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ChatMessagesStreamingCompatibilityTests
{
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

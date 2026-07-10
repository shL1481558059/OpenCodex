using System.Text;
using System.Text.Json;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ResponsesOutboundStreamingCompatibilityTests
{
    [Fact]
    public async Task ResponsesToChat_FunctionCallFinishesWithToolCalls()
    {
        var events = await CollectAsync(SseStreamConverter.ResponsesToChatEvents(
            Lines(
                Event("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"lookup","arguments":""}}"""),
                Event("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{}"}"""),
                Event("response.completed", """{"type":"response.completed","response":{"id":"resp_1","status":"completed","model":"gpt-5","usage":{}}}""")),
            "gpt-5",
            new ConvertedStreamResult(),
            CancellationToken.None));

        Assert.Contains(ParseChatPayloads(events), payload =>
            FirstChoice(payload)?["finish_reason"]?.ToString() == "tool_calls");
    }

    [Fact]
    public async Task ResponsesToChat_IncompleteTerminalEventFinishesWithLength()
    {
        var events = await CollectAsync(SseStreamConverter.ResponsesToChatEvents(
            Lines(
                Event("response.output_text.delta", """{"type":"response.output_text.delta","output_index":0,"delta":"partial"}"""),
                Event("response.incomplete", """{"type":"response.incomplete","response":{"id":"resp_1","status":"incomplete","model":"gpt-5","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":1,"output_tokens":2}}}""")),
            "gpt-5",
            new ConvertedStreamResult(),
            CancellationToken.None));

        Assert.Contains(ParseChatPayloads(events), payload =>
            FirstChoice(payload)?["finish_reason"]?.ToString() == "length");
    }

    [Fact]
    public async Task ResponsesToMessages_IncompleteTerminalEventFinishesWithMaxTokens()
    {
        var events = await CollectAsync(SseStreamConverter.ResponsesToMessagesEvents(
            Lines(
                Event("response.output_text.delta", """{"type":"response.output_text.delta","output_index":0,"delta":"partial"}"""),
                Event("response.incomplete", """{"type":"response.incomplete","response":{"id":"resp_1","status":"incomplete","model":"gpt-5","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":1,"output_tokens":2}}}""")),
            "gpt-5",
            new ConvertedStreamResult(),
            CancellationToken.None));

        var delta = ParseNamedEvents(events).Single(entry => entry.Type == "message_delta").Payload;
        Assert.Equal("max_tokens", Object(delta["delta"])["stop_reason"]?.ToString());
    }

    [Fact]
    public async Task ResponsesToChat_FailedResponseEmitsErrorWithoutNormalDone()
    {
        var result = new ConvertedStreamResult();
        var events = await CollectAsync(SseStreamConverter.ResponsesToChatEvents(
            Lines(Event("response.failed", """{"type":"response.failed","response":{"id":"resp_failed","status":"failed","model":"gpt-5","error":{"code":"server_error","message":"upstream failed"}}}""")),
            "gpt-5",
            result,
            CancellationToken.None));

        Assert.Contains(events, line => line.Contains("\"error\"", StringComparison.Ordinal)
            && line.Contains("upstream failed", StringComparison.Ordinal));
        Assert.DoesNotContain(events, line => line.Contains("[DONE]", StringComparison.Ordinal));
        Assert.Equal("failed", result.UpstreamResponse?["status"]?.ToString());
    }

    [Fact]
    public async Task ResponsesToMessages_FailedResponseEmitsErrorWithoutMessageStop()
    {
        var events = await CollectAsync(SseStreamConverter.ResponsesToMessagesEvents(
            Lines(Event("response.failed", """{"type":"response.failed","response":{"id":"resp_failed","status":"failed","model":"gpt-5","error":{"type":"api_error","message":"upstream failed"}}}""")),
            "gpt-5",
            new ConvertedStreamResult(),
            CancellationToken.None));

        var parsed = ParseNamedEvents(events);
        Assert.Contains(parsed, entry => entry.Type == "error"
            && JsonSerializer.Serialize(entry.Payload).Contains("upstream failed", StringComparison.Ordinal));
        Assert.DoesNotContain(parsed, entry => entry.Type == "message_stop");
    }

    [Fact]
    public async Task ResponsesToChat_RefusalAndAnnotationRemainStructured()
    {
        var events = await CollectAsync(SseStreamConverter.ResponsesToChatEvents(
            Lines(
                Event("response.refusal.delta", """{"type":"response.refusal.delta","output_index":0,"content_index":0,"delta":"I cannot"}"""),
                Event("response.refusal.done", """{"type":"response.refusal.done","output_index":0,"content_index":0,"refusal":"I cannot help"}"""),
                Event("response.output_text.annotation.added", """{"type":"response.output_text.annotation.added","output_index":0,"content_index":0,"annotation_index":0,"annotation":{"type":"url_citation","url":"https://example.com","title":"Example","start_index":0,"end_index":7}}"""),
                Event("response.completed", """{"type":"response.completed","response":{"status":"completed","usage":{}}}""")),
            "gpt-5",
            new ConvertedStreamResult(),
            CancellationToken.None));

        var chunks = ParseChatPayloads(events);
        Assert.Contains(chunks, payload => Value(FirstDelta(payload), "refusal")?.ToString() == "I cannot");
        Assert.Contains(chunks, payload => Value(FirstDelta(payload), "refusal")?.ToString() == " help");
        Assert.Contains(chunks, payload => FirstDelta(payload)?.ContainsKey("annotations") == true);
    }

    [Fact]
    public async Task ResponsesToMessages_RefusalAndUrlAnnotationAreRepresented()
    {
        var events = await CollectAsync(SseStreamConverter.ResponsesToMessagesEvents(
            Lines(
                Event("response.refusal.delta", """{"type":"response.refusal.delta","output_index":0,"content_index":0,"delta":"I cannot"}"""),
                Event("response.output_text.annotation.added", """{"type":"response.output_text.annotation.added","output_index":0,"content_index":0,"annotation_index":0,"annotation":{"type":"url_citation","url":"https://example.com","title":"Example","start_index":0,"end_index":7}}"""),
                Event("response.completed", """{"type":"response.completed","response":{"status":"completed","usage":{}}}""")),
            "gpt-5",
            new ConvertedStreamResult(),
            CancellationToken.None));

        var deltas = ParseNamedEvents(events)
            .Where(entry => entry.Type == "content_block_delta")
            .Select(entry => Object(entry.Payload["delta"]))
            .ToList();
        Assert.Contains(deltas, delta => delta["type"]?.ToString() == "text_delta"
            && delta["text"]?.ToString() == "I cannot");
        var citation = Assert.Single(deltas, delta => delta["type"]?.ToString() == "citations_delta");
        Assert.Equal("https://example.com", Object(citation["citation"])["url"]?.ToString());
    }

    [Fact]
    public async Task ResponsesToChat_NativeMcpCallThrowsExplicitly()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(
            SseStreamConverter.ResponsesToChatEvents(
                Lines(Event("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"mcp_1","type":"mcp_call","server_label":"weather","name":"forecast","arguments":"{}"}}""")),
                "gpt-5",
                new ConvertedStreamResult(),
                CancellationToken.None)));

        Assert.Contains("mcp_call", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mcp_list_tools", "chat")]
    [InlineData("mcp_approval_request", "chat")]
    [InlineData("mcp_approval_response", "chat")]
    [InlineData("mcp_list_tools", "messages")]
    [InlineData("mcp_approval_request", "messages")]
    [InlineData("mcp_approval_response", "messages")]
    public async Task ResponsesOutbound_UnsupportedMcpLifecycleThrowsExplicitly(string itemType, string target)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "response.output_item.added",
            ["output_index"] = 0,
            ["item"] = new Dictionary<string, object?>
            {
                ["id"] = "mcp_event_1",
                ["type"] = itemType,
                ["server_label"] = "weather"
            }
        });
        var exception = target == "chat"
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(
                SseStreamConverter.ResponsesToChatEvents(
                    Lines(Event("response.output_item.added", json)),
                    "gpt-5",
                    new ConvertedStreamResult(),
                    CancellationToken.None)))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(
                SseStreamConverter.ResponsesToMessagesEvents(
                    Lines(Event("response.output_item.added", json)),
                    "claude-sonnet",
                    new ConvertedStreamResult(),
                    CancellationToken.None)));

        Assert.Contains(itemType, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponsesToMessages_NativeMcpCallMapsUseAndResult()
    {
        var events = await CollectAsync(SseStreamConverter.ResponsesToMessagesEvents(
            Lines(
                Event("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"mcp_1","type":"mcp_call","server_label":"weather","name":"forecast","arguments":"{\"city\":\"Shanghai\"}","status":"in_progress"}}"""),
                Event("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"id":"mcp_1","type":"mcp_call","server_label":"weather","name":"forecast","arguments":"{\"city\":\"Shanghai\"}","output":"sunny","status":"completed"}}"""),
                Event("response.completed", """{"type":"response.completed","response":{"status":"completed","usage":{}}}""")),
            "claude-sonnet",
            new ConvertedStreamResult(),
            CancellationToken.None));

        var starts = ParseNamedEvents(events)
            .Where(entry => entry.Type == "content_block_start")
            .Select(entry => Object(entry.Payload["content_block"]))
            .ToList();
        var use = Assert.Single(starts, block => block["type"]?.ToString() == "mcp_tool_use");
        Assert.Equal("weather", use["server_name"]?.ToString());
        Assert.Equal("forecast", use["name"]?.ToString());
        var toolResult = Assert.Single(starts, block => block["type"]?.ToString() == "mcp_tool_result");
        Assert.Equal("mcp_1", toolResult["tool_use_id"]?.ToString());
        Assert.False(Assert.IsType<JsonElement>(toolResult["is_error"]).GetBoolean());

        var messageDelta = ParseNamedEvents(events).Single(entry => entry.Type == "message_delta").Payload;
        Assert.Equal("end_turn", Object(messageDelta["delta"])["stop_reason"]?.ToString());
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    public async Task ResponsesOutbound_ApplyPatchRawStringInputBecomesPatchObject(string target)
    {
        const string patch = "*** Begin Patch\n*** Add File: note.txt\n+hello\n*** End Patch";
        var lines = Lines(
            Event("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"patch_1","type":"custom_tool_call","call_id":"call_patch","name":"apply_patch","input":"","status":"in_progress"}}"""),
            Event("response.custom_tool_call_input.delta", JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "response.custom_tool_call_input.delta",
                ["output_index"] = 0,
                ["delta"] = patch
            })),
            Event("response.output_item.done", JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = 0,
                ["item"] = new Dictionary<string, object?>
                {
                    ["id"] = "patch_1",
                    ["type"] = "custom_tool_call",
                    ["call_id"] = "call_patch",
                    ["name"] = "apply_patch",
                    ["input"] = patch,
                    ["status"] = "completed"
                }
            })),
            Event("response.completed", """{"type":"response.completed","response":{"status":"completed","usage":{}}}"""));

        string argumentsJson;
        if (target == "chat")
        {
            var events = await CollectAsync(SseStreamConverter.ResponsesToChatEvents(
                lines, "gpt-5", new ConvertedStreamResult(), CancellationToken.None));
            var argumentDeltas = ParseChatPayloads(events)
                .Select(FirstDelta)
                .Where(delta => Value(delta, "tool_calls") is JsonElement { ValueKind: JsonValueKind.Array } calls
                    && calls.EnumerateArray().Any(call => call.TryGetProperty("function", out var function)
                        && function.TryGetProperty("arguments", out _)))
                .Select(delta => Assert.IsType<JsonElement>(Value(delta, "tool_calls"))
                    .EnumerateArray()
                    .Select(call => call.GetProperty("function"))
                    .First(function => function.TryGetProperty("arguments", out _))
                    .GetProperty("arguments")
                    .GetString()!)
                .ToList();
            argumentsJson = Assert.Single(argumentDeltas, arguments => arguments.Length > 0);
        }
        else
        {
            var events = await CollectAsync(SseStreamConverter.ResponsesToMessagesEvents(
                lines, "claude-sonnet", new ConvertedStreamResult(), CancellationToken.None));
            argumentsJson = Assert.Single(ParseNamedEvents(events)
                .Where(entry => entry.Type == "content_block_delta")
                .Select(entry => Object(entry.Payload["delta"]))
                .Where(delta => delta["type"]?.ToString() == "input_json_delta")
                .Select(delta => delta["partial_json"]?.ToString() ?? string.Empty));
        }

        using var arguments = JsonDocument.Parse(argumentsJson);
        Assert.Equal(JsonValueKind.Object, arguments.RootElement.ValueKind);
        Assert.Equal(patch, arguments.RootElement.GetProperty("patch").GetString());
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    public async Task ResponsesOutbound_ToolSearchRemainsAClientToolCall(string target)
    {
        var lines = Lines(
            Event("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"ts_1","type":"tool_search_call","call_id":"call_ts","name":"tool_search","execution":"client","arguments":{}}}"""),
            Event("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"query\":\"browser\"}"}"""),
            Event("response.function_call_arguments.done", """{"type":"response.function_call_arguments.done","output_index":0,"arguments":"{\"query\":\"browser\"}"}"""),
            Event("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"id":"ts_1","type":"tool_search_call","call_id":"call_ts","name":"tool_search","execution":"client","arguments":{"query":"browser"},"status":"completed"}}"""),
            Event("response.completed", """{"type":"response.completed","response":{"status":"completed","usage":{}}}"""));

        if (target == "chat")
        {
            var events = await CollectAsync(SseStreamConverter.ResponsesToChatEvents(
                lines, "gpt-5", new ConvertedStreamResult(), CancellationToken.None));
            var payloads = ParseChatPayloads(events).ToList();
            var argumentChunks = payloads
                .Select(FirstDelta)
                .Select(delta => Value(delta, "tool_calls"))
                .OfType<JsonElement>()
                .Where(calls => calls.ValueKind == JsonValueKind.Array)
                .SelectMany(calls => calls.EnumerateArray())
                .Where(call => call.TryGetProperty("function", out var function)
                    && function.TryGetProperty("arguments", out _))
                .Select(call => call.GetProperty("function").GetProperty("arguments"))
                .ToList();
            Assert.NotEmpty(argumentChunks);
            Assert.All(argumentChunks, argument => Assert.Equal(JsonValueKind.String, argument.ValueKind));
            Assert.Equal(
                "{\"query\":\"browser\"}",
                string.Concat(argumentChunks.Select(argument => argument.GetString())));
            Assert.Contains(payloads, payload => FirstChoice(payload)?["finish_reason"]?.ToString() == "tool_calls");
            return;
        }

        var messageEvents = await CollectAsync(SseStreamConverter.ResponsesToMessagesEvents(
            lines, "claude-sonnet", new ConvertedStreamResult(), CancellationToken.None));
        Assert.Contains(ParseNamedEvents(messageEvents), entry => entry.Type == "content_block_start"
            && Object(entry.Payload["content_block"])["type"]?.ToString() == "tool_use");
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    public async Task ResponsesOutbound_ServerExecutedWebSearchKeepsFinalAnswerFlow(string target)
    {
        var lines = Lines(
            Event("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"ws_1","type":"web_search_call","status":"in_progress"}}"""),
            Event("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"id":"ws_1","type":"web_search_call","status":"completed","action":{"type":"search","query":"OpenCodex"}}}"""),
            Event("response.output_text.delta", """{"type":"response.output_text.delta","output_index":1,"delta":"search result"}"""),
            Event("response.completed", """{"type":"response.completed","response":{"status":"completed","usage":{}}}"""));

        if (target == "chat")
        {
            var result = new ConvertedStreamResult();
            var events = await CollectAsync(SseStreamConverter.ResponsesToChatEvents(
                lines, "gpt-5", result, CancellationToken.None));
            Assert.Contains(ParseChatPayloads(events), payload => Value(FirstDelta(payload), "content")?.ToString() == "search result");
            Assert.Contains(ParseChatPayloads(events), payload => FirstChoice(payload)?["finish_reason"]?.ToString() == "stop");
            Assert.Contains(Assert.IsType<List<object?>>(result.UpstreamResponse!["output"]), item =>
                item is Dictionary<string, object?> dictionary && dictionary["type"]?.ToString() == "web_search_call");
            return;
        }

        var messageResult = new ConvertedStreamResult();
        var messageEvents = await CollectAsync(SseStreamConverter.ResponsesToMessagesEvents(
            lines, "claude-sonnet", messageResult, CancellationToken.None));
        Assert.Contains(ParseNamedEvents(messageEvents), entry => entry.Type == "content_block_delta"
            && Value(Object(entry.Payload["delta"]), "text")?.ToString() == "search result");
        Assert.Contains(Assert.IsType<List<object?>>(messageResult.UpstreamResponse!["output"]), item =>
            item is Dictionary<string, object?> dictionary && dictionary["type"]?.ToString() == "web_search_call");
    }

    private static async IAsyncEnumerable<string> Lines(params string[] blocks)
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

    private static string Event(string eventName, string json)
        => $"event: {eventName}\ndata: {json}\n\n";

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> events)
    {
        var result = new List<string>();
        await foreach (var entry in events)
        {
            result.Add(entry);
        }

        return result;
    }

    private static List<Dictionary<string, object?>> ParseChatPayloads(IEnumerable<string> events)
        => events
            .SelectMany(SplitBlocks)
            .Select(block => block.Split('\n').FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal)))
            .Where(line => line is not null && !line.Contains("[DONE]", StringComparison.Ordinal))
            .Select(line => Deserialize(line!["data:".Length..].Trim()))
            .ToList();

    private static List<(string Type, Dictionary<string, object?> Payload)> ParseNamedEvents(IEnumerable<string> events)
    {
        var parsed = new List<(string, Dictionary<string, object?>)>();
        foreach (var block in events.SelectMany(SplitBlocks))
        {
            var lines = block.Split('\n');
            var eventName = lines.FirstOrDefault(line => line.StartsWith("event:", StringComparison.Ordinal))?["event:".Length..].Trim();
            var data = lines.FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal))?["data:".Length..].Trim();
            if (eventName is not null && data is not null)
            {
                parsed.Add((eventName, Deserialize(data)));
            }
        }

        return parsed;
    }

    private static IEnumerable<string> SplitBlocks(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

    private static Dictionary<string, object?> Deserialize(string json)
        => JsonSerializer.Deserialize<Dictionary<string, object?>>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        })!;

    private static Dictionary<string, object?>? FirstChoice(Dictionary<string, object?> payload)
    {
        if (payload.TryGetValue("choices", out var choicesValue)
            && choicesValue is JsonElement choices
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(choices[0].GetRawText());
        }

        return null;
    }

    private static Dictionary<string, object?>? FirstDelta(Dictionary<string, object?> payload)
    {
        var choice = FirstChoice(payload);
        return choice is null ? null : Object(choice["delta"]);
    }

    private static Dictionary<string, object?> Object(object? value)
    {
        if (value is Dictionary<string, object?> dictionary)
        {
            return dictionary;
        }

        return value is JsonElement element
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText())!
            : throw new InvalidOperationException("Expected an object.");
    }

    private static object? Value(Dictionary<string, object?>? value, string key)
        => value is not null && value.TryGetValue(key, out var item) ? item : null;
}

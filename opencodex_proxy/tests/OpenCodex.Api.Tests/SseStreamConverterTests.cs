using System.Text;
using System.Text.Json;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class SseStreamConverterTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    // ── helpers ──────────────────────────────────────────────

    /// <summary>Yield each line individually so ParseEvents gets them one at a time.</summary>
    private static async IAsyncEnumerable<string> SseLines(params string[] sseBlocks)
    {
        foreach (var block in sseBlocks)
        {
            foreach (var line in block.Split('\n'))
            {
                yield return line;
            }
        }

        await Task.CompletedTask;
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> enumerable)
    {
        var list = new List<string>();
        await foreach (var s in enumerable)
        {
            list.Add(s);
        }

        return list;
    }

    /// <summary>A single SSE event block. Must end with a blank line separator.</summary>
    private static string SseBlock(string data, string? eventName = null)
    {
        var sb = new StringBuilder();
        if (eventName is not null)
        {
            sb.Append("event: ").Append(eventName).Append('\n');
        }

        sb.Append("data: ").Append(data).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }

    private static string ChatChunk(
        string? content = null,
        string? reasoningContent = null,
        object? toolCalls = null,
        string? finishReason = null,
        Dictionary<string, object?>? usage = null,
        string id = "chatcmpl-1",
        string model = "gpt-5",
        long created = 1700000000)
    {
        var delta = new Dictionary<string, object?>();
        if (content is not null) delta["content"] = content;
        if (reasoningContent is not null) delta["reasoning_content"] = reasoningContent;
        if (toolCalls is not null) delta["tool_calls"] = toolCalls;

        var choice = new Dictionary<string, object?>
        {
            ["index"] = 0,
            ["delta"] = delta,
            ["finish_reason"] = finishReason
        };

        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new List<object?> { choice }
        };
        if (usage is not null) payload["usage"] = usage;

        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    private static Dictionary<string, object?> ParseJsonEvent(string sseLine)
    {
        var idx = sseLine.IndexOf("data: ", StringComparison.Ordinal);
        var json = sseLine[(idx + "data: ".Length)..].TrimEnd('\n');
        using var doc = JsonDocument.Parse(json);
        return ElementToDict(doc.RootElement)!;
    }

    private static object? ElementToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => ElementToDict(e),
        JsonValueKind.Array => e.EnumerateArray().Select(ElementToObject).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l)
            ? l is >= int.MinValue and <= int.MaxValue ? (object)(int)l : l
            : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static Dictionary<string, object?> ElementToDict(JsonElement e) =>
        e.EnumerateObject().ToDictionary(p => p.Name, p => ElementToObject(p.Value), StringComparer.Ordinal);

    private static List<Dictionary<string, object?>> ParseEvents(List<string> lines)
    {
        var events = new List<Dictionary<string, object?>>();
        foreach (var line in lines)
        {
            if (line.Contains("data:", StringComparison.Ordinal))
            {
                events.Add(ParseJsonEvent(line));
            }
        }

        return events;
    }

    private static Dictionary<string, object?>? ByType(List<Dictionary<string, object?>> events, string type) =>
        events.FirstOrDefault(e => e.TryGetValue("type", out var t) && t?.ToString() == type);

    // ── Chat → Responses tests ───────────────────────────────

    [Fact]
    public async Task ReasoningContent_EmitsEvents()
    {
        var lines = SseLines(
            SseBlock(ChatChunk(reasoningContent: "Let me think")),
            SseBlock(ChatChunk(reasoningContent: " about this")),
            SseBlock(ChatChunk(content: "The answer is 42")),
            SseBlock(ChatChunk(
                finishReason: "stop",
                usage: new Dictionary<string, object?> { ["prompt_tokens"] = 10, ["completion_tokens"] = 20 })),
            SseBlock("[DONE]"));

        var result = new ConvertedStreamResult();
        var events = await CollectAsync(
            SseStreamConverter.ChatToResponsesEvents(lines, "gpt-5", result, CancellationToken.None));

        var parsed = ParseEvents(events);
        Assert.NotNull(ByType(parsed, "response.reasoning_summary_text.delta"));

        var completed = ByType(parsed, "response.completed");
        Assert.NotNull(completed);
        var output = ((Dictionary<string, object?>)completed!["response"]!)["output"] as List<object?>;
        Assert.NotNull(output);
        Assert.Contains(output!, i => i is Dictionary<string, object?> d && d.TryGetValue("type", out var t) && "reasoning".Equals(t));
    }

    [Fact]
    public async Task NoReasoning_NoReasoningEvents()
    {
        var lines = SseLines(
            SseBlock(ChatChunk(content: "Hello")),
            SseBlock(ChatChunk(content: " world")),
            SseBlock(ChatChunk(
                finishReason: "stop",
                usage: new Dictionary<string, object?> { ["prompt_tokens"] = 5, ["completion_tokens"] = 2 })),
            SseBlock("[DONE]"));

        var result = new ConvertedStreamResult();
        var events = await CollectAsync(
            SseStreamConverter.ChatToResponsesEvents(lines, "gpt-5", result, CancellationToken.None));

        var parsed = ParseEvents(events);
        Assert.Null(ByType(parsed, "response.reasoning_summary_text.delta"));
        Assert.Null(ByType(parsed, "response.reasoning_summary_text.done"));

        var completed = ByType(parsed, "response.completed");
        Assert.NotNull(completed);
        var output = ((Dictionary<string, object?>)completed!["response"]!)["output"] as List<object?>;
        Assert.NotNull(output);
        Assert.DoesNotContain(output!, i => i is Dictionary<string, object?> d && d.TryGetValue("type", out var t) && "reasoning".Equals(t));
    }

    [Fact]
    public async Task ReasoningContent_StoredInUpstreamResponse()
    {
        var lines = SseLines(
            SseBlock(ChatChunk(reasoningContent: "Thinking step 1.")),
            SseBlock(ChatChunk(content: "Output text")),
            SseBlock(ChatChunk(
                finishReason: "stop",
                usage: new Dictionary<string, object?> { ["prompt_tokens"] = 5, ["completion_tokens"] = 10 })),
            SseBlock("[DONE]"));

        var result = new ConvertedStreamResult();
        _ = await CollectAsync(
            SseStreamConverter.ChatToResponsesEvents(lines, "gpt-5", result, CancellationToken.None));

        Assert.NotNull(result.UpstreamResponse);
        var choices = result.UpstreamResponse!["choices"] as List<object?>;
        Assert.NotEmpty(choices!);
        var message = ((Dictionary<string, object?>)choices![0]!)["message"] as Dictionary<string, object?>;
        Assert.NotNull(message);
        Assert.Equal("Thinking step 1.", message!["reasoning_content"]?.ToString());
    }

    // ── Messages → Responses tests ───────────────────────────

    [Fact]
    public async Task TextOnly_NoFunctionCallInOutput()
    {
        var lines = SseLines(
            SseBlock("""{"type":"message_start","message":{"id":"msg_1","model":"claude-3","usage":{"input_tokens":5,"output_tokens":0}}}""", "message_start"),
            SseBlock("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""", "content_block_start"),
            SseBlock("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}""", "content_block_delta"),
            SseBlock("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}""", "content_block_delta"),
            SseBlock("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}""", "message_delta"),
            SseBlock("""{"type":"message_stop"}""", "message_stop"));

        var result = new ConvertedStreamResult();
        var events = await CollectAsync(
            SseStreamConverter.MessagesToResponsesEvents(lines, "claude-3", result, CancellationToken.None));

        var parsed = ParseEvents(events);
        var completed = ByType(parsed, "response.completed");
        Assert.NotNull(completed);
        var output = ((Dictionary<string, object?>)completed!["response"]!)["output"] as List<object?>;
        Assert.NotNull(output);
        Assert.Contains(output!, i => i is Dictionary<string, object?> d && d.TryGetValue("type", out var t) && "message".Equals(t));
        Assert.DoesNotContain(output!, i => i is Dictionary<string, object?> d && d.TryGetValue("type", out var t) && "function_call".Equals(t));
    }

    [Fact]
    public async Task ToolUse_IncludesFunctionCallInOutput()
    {
        var lines = SseLines(
            SseBlock("""{"type":"message_start","message":{"id":"msg_1","model":"claude-3","usage":{"input_tokens":5,"output_tokens":0}}}""", "message_start"),
            SseBlock("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"Let me search"}}""", "content_block_start"),
            SseBlock("""{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"web_search","input":{}}}""", "content_block_start"),
            SseBlock("""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"query\":"}}""", "content_block_delta"),
            SseBlock("""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"test\"}"}}""", "content_block_delta"),
            SseBlock("""{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":20}}""", "message_delta"),
            SseBlock("""{"type":"message_stop"}""", "message_stop"));

        var result = new ConvertedStreamResult();
        var events = await CollectAsync(
            SseStreamConverter.MessagesToResponsesEvents(lines, "claude-3", result, CancellationToken.None));

        var parsed = ParseEvents(events);
        var completed = ByType(parsed, "response.completed");
        Assert.NotNull(completed);
        var output = ((Dictionary<string, object?>)completed!["response"]!)["output"] as List<object?>;
        Assert.NotNull(output);

        var fc = output!.FirstOrDefault(i => i is Dictionary<string, object?> d && d.TryGetValue("type", out var t) && "function_call".Equals(t)) as Dictionary<string, object?>;
        Assert.NotNull(fc);
        Assert.Equal("toolu_1", fc!["call_id"]?.ToString());
        Assert.Equal("web_search", fc["name"]?.ToString());
        Assert.NotNull(fc["arguments"]);
    }
}

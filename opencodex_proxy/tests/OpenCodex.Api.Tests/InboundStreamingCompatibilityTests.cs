using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class InboundStreamingCompatibilityTests
{
    [Fact]
    public async Task MessagesToResponses_MaxTokensProducesIncompleteResponse()
    {
        var events = await CollectAsync(SseStreamConverter.MessagesToResponsesEvents(
            Lines(
                Event("message_start", """{"type":"message_start","message":{"id":"msg_1","model":"claude","usage":{"input_tokens":1,"output_tokens":0}}}"""),
                Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
                Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"partial"}}"""),
                Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"max_tokens"},"usage":{"output_tokens":2}}"""),
                Event("message_stop", """{"type":"message_stop"}""")),
            "claude",
            new ConvertedStreamResult(),
            CancellationToken.None));

        var body = string.Concat(events);
        Assert.Contains("\"status\":\"incomplete\"", body, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"max_output_tokens\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatToResponses_CustomToolCallRemainsCustom()
    {
        var events = await CollectAsync(SseStreamConverter.ChatToResponsesEvents(
            Lines(
                Data("""{"id":"chat_1","model":"gpt","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"custom_1","type":"custom","custom":{"name":"shell_text","input":"hello"}}]},"finish_reason":null}]}"""),
                Data("""{"id":"chat_1","model":"gpt","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""),
                "data: [DONE]\n\n"),
            "gpt",
            new ConvertedStreamResult(),
            CancellationToken.None));

        var body = string.Concat(events);
        Assert.Contains("\"type\":\"custom_tool_call\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"shell_text\"", body, StringComparison.Ordinal);
        Assert.Contains("\"input\":\"hello\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessagesToResponses_McpUseAndResultBecomeMcpCall()
    {
        var events = await CollectAsync(SseStreamConverter.MessagesToResponsesEvents(
            Lines(
                Event("message_start", """{"type":"message_start","message":{"id":"msg_1","model":"claude","usage":{"input_tokens":1,"output_tokens":0}}}"""),
                Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"mcp_tool_use","id":"mcp_1","name":"forecast","server_name":"weather","input":{"city":"Shanghai"}}}"""),
                Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
                Event("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"mcp_tool_result","tool_use_id":"mcp_1","is_error":false,"content":[{"type":"text","text":"sunny"}]}}"""),
                Event("content_block_stop", """{"type":"content_block_stop","index":1}"""),
                Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}"""),
                Event("message_stop", """{"type":"message_stop"}""")),
            "claude",
            new ConvertedStreamResult(),
            CancellationToken.None));

        var body = string.Concat(events);
        Assert.Contains("\"type\":\"mcp_call\"", body, StringComparison.Ordinal);
        Assert.Contains("\"server_label\":\"weather\"", body, StringComparison.Ordinal);
        Assert.Contains("\"output\":\"sunny\"", body, StringComparison.Ordinal);
    }

    private static string Event(string name, string json) => $"event: {name}\ndata: {json}\n\n";
    private static string Data(string json) => $"data: {json}\n\n";

    private static async IAsyncEnumerable<string> Lines(params string[] lines)
    {
        foreach (var block in lines)
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
        await foreach (var line in source)
        {
            result.Add(line);
        }

        return result;
    }
}

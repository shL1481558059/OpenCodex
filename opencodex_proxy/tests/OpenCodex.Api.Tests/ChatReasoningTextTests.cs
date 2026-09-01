using System.Text;
using System.Text.Json;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

/// <summary>
/// 回归覆盖 OpenRouter 风格上游（api.commandcode.ai 等）的思维链字段：
/// 增量放在 <c>delta.reasoning</c> 与 <c>delta.reasoning_details[]</c>，而不是 <c>reasoning_content</c>。
/// </summary>
public sealed class ChatReasoningTextTests
{
    [Fact]
    public void Extract_PrefersReasoningContentOverOtherFields()
    {
        var text = ChatReasoningText.Extract(new Dictionary<string, object?>
        {
            ["reasoning_content"] = "official",
            ["reasoning"] = "openrouter",
            ["reasoning_details"] = Details("details")
        });

        Assert.Equal("official", text);
    }

    [Fact]
    public void Extract_UsesReasoningWithoutDuplicatingDetails()
    {
        var text = ChatReasoningText.Extract(new Dictionary<string, object?>
        {
            ["reasoning"] = "thinking",
            ["reasoning_details"] = Details("thinking")
        });

        Assert.Equal("thinking", text);
    }

    [Fact]
    public void Extract_FallsBackToReasoningDetails()
    {
        var text = ChatReasoningText.Extract(new Dictionary<string, object?>
        {
            ["reasoning_details"] = new List<object?>
            {
                Detail("reasoning.text", "step one "),
                Detail("reasoning.text", "step two")
            }
        });

        Assert.Equal("step one step two", text);
    }

    [Fact]
    public void Extract_SkipsDetailsWithoutPlainText()
    {
        var text = ChatReasoningText.Extract(new Dictionary<string, object?>
        {
            ["reasoning_details"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "reasoning.encrypted",
                    ["data"] = "b3BhcXVl"
                }
            }
        });

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Extract_ReturnsEmptyWhenNoReasoningFields()
    {
        Assert.Equal(string.Empty, ChatReasoningText.Extract(new Dictionary<string, object?>
        {
            ["content"] = "hello"
        }));
        Assert.Equal(string.Empty, ChatReasoningText.Extract(null));
    }

    [Fact]
    public async Task ChatToResponses_OpenRouterReasoning_EmitsSummaryAndPersistsUpstreamBody()
    {
        var lines = SseLines(
            SseBlock(ReasoningChunk("Let me ")),
            SseBlock(ReasoningChunk("think.")),
            SseBlock(ContentChunk("Answer.")),
            SseBlock(FinishChunk()),
            SseBlock("[DONE]"));

        var result = new ConvertedStreamResult();
        var events = await CollectAsync(
            SseStreamConverter.ChatToResponsesEvents(lines, "deepseek-v4-flash", result, CancellationToken.None));

        var parsed = ParseEvents(events);
        var summaryDeltas = parsed
            .Where(item => item.TryGetValue("type", out var type)
                && type?.ToString() == "response.reasoning_summary_text.delta")
            .Select(item => item["delta"]?.ToString())
            .ToList();
        Assert.Equal(["Let me ", "think."], summaryDeltas);

        // 转换后响应：response.completed 里必须带 reasoning item。
        var completed = Assert.IsType<Dictionary<string, object?>>(
            parsed.Single(item => item.TryGetValue("type", out var type)
                && type?.ToString() == "response.completed")["response"]);
        var output = Assert.IsType<List<object?>>(completed["output"]);
        var reasoningItem = output
            .OfType<Dictionary<string, object?>>()
            .Single(item => item["type"]?.ToString() == "reasoning");
        var summary = Assert.IsType<List<object?>>(reasoningItem["summary"]);
        var summaryText = Assert.IsType<Dictionary<string, object?>>(summary[0]);
        Assert.Equal("Let me think.", summaryText["text"]?.ToString());

        // 转换前响应：由流重建出的上游 body 要带 reasoning_content。
        Assert.NotNull(result.UpstreamResponse);
        var choices = Assert.IsType<List<object?>>(result.UpstreamResponse!["choices"]);
        var message = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<Dictionary<string, object?>>(choices[0])["message"]);
        Assert.Equal("Let me think.", message["reasoning_content"]?.ToString());
        Assert.Equal("Answer.", message["content"]?.ToString());
    }

    [Fact]
    public async Task ChatToMessages_OpenRouterReasoning_EmitsThinkingDelta()
    {
        var lines = SseLines(
            SseBlock(DetailsOnlyChunk("hmm")),
            SseBlock(ContentChunk("done")),
            SseBlock(FinishChunk()),
            SseBlock("[DONE]"));

        var result = new ConvertedStreamResult();
        var events = await CollectAsync(
            SseStreamConverter.ChatToMessagesEvents(lines, "deepseek-v4-flash", result, CancellationToken.None));

        var thinking = ParseEvents(events)
            .Where(item => item.TryGetValue("type", out var type) && type?.ToString() == "content_block_delta")
            .Select(item => Assert.IsType<Dictionary<string, object?>>(item["delta"]))
            .Where(delta => delta["type"]?.ToString() == "thinking_delta")
            .Select(delta => delta["thinking"]?.ToString())
            .ToList();

        Assert.Equal(["hmm"], thinking);
    }

    [Fact]
    public void ConvertResponse_NonStreamOpenRouterReasoning_ProducesResponsesReasoningItem()
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = "gen_1",
            ["object"] = "chat.completion",
            ["model"] = "deepseek/deepseek-v4-flash",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = "Answer.",
                        ["reasoning"] = "Deliberating.",
                        ["reasoning_details"] = Details("Deliberating.")
                    },
                    ["finish_reason"] = "stop"
                }
            }
        };

        var response = ProtocolConverter.ConvertResponse(
            payload,
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "deepseek-v4-flash");

        var output = Assert.IsType<List<object?>>(response["output"]);
        var reasoningItem = output
            .OfType<Dictionary<string, object?>>()
            .Single(item => item["type"]?.ToString() == "reasoning");
        var summary = Assert.IsType<List<object?>>(reasoningItem["summary"]);
        var summaryText = Assert.IsType<Dictionary<string, object?>>(summary[0]);
        Assert.Equal("Deliberating.", summaryText["text"]?.ToString());
    }

    // ── helpers ──────────────────────────────────────────────

    private static Dictionary<string, object?> Detail(string type, string text) => new()
    {
        ["type"] = type,
        ["text"] = text,
        ["format"] = "unknown",
        ["index"] = 0
    };

    private static List<object?> Details(string text) => [Detail("reasoning.text", text)];

    private static string ReasoningChunk(string text) => Chunk(new Dictionary<string, object?>
    {
        ["reasoning"] = text,
        ["reasoning_details"] = Details(text)
    });

    private static string DetailsOnlyChunk(string text) => Chunk(new Dictionary<string, object?>
    {
        ["reasoning_details"] = Details(text)
    });

    private static string ContentChunk(string text) => Chunk(new Dictionary<string, object?>
    {
        ["content"] = text
    });

    private static string FinishChunk() => Chunk(
        new Dictionary<string, object?>(),
        finishReason: "stop",
        usage: new Dictionary<string, object?>
        {
            ["prompt_tokens"] = 12,
            ["completion_tokens"] = 34,
            ["completion_tokens_details"] = new Dictionary<string, object?> { ["reasoning_tokens"] = 20 }
        });

    private static string Chunk(
        Dictionary<string, object?> delta,
        string? finishReason = null,
        Dictionary<string, object?>? usage = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = "gen_stream",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1700000000,
            ["model"] = "deepseek/deepseek-v4-flash",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finishReason
                }
            }
        };
        if (usage is not null)
        {
            payload["usage"] = usage;
        }

        return JsonSerializer.Serialize(payload);
    }

    private static string SseBlock(string data)
    {
        var builder = new StringBuilder();
        builder.Append("data: ").Append(data).Append('\n').Append('\n');
        return builder.ToString();
    }

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
        var list = new List<string>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ParseEvents(List<string> lines)
    {
        var events = new List<Dictionary<string, object?>>();
        foreach (var line in lines)
        {
            var index = line.IndexOf("data:", StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var json = line[(index + "data:".Length)..].Trim();
            if (json.Length == 0 || json == "[DONE]")
            {
                continue;
            }

            using var document = JsonDocument.Parse(json);
            events.Add(ToDictionary(document.RootElement));
        }

        return events;
    }

    private static Dictionary<string, object?> ToDictionary(JsonElement element) =>
        element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => ToValue(property.Value),
            StringComparer.Ordinal);

    private static object? ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToDictionary(element),
        JsonValueKind.Array => element.EnumerateArray().Select(ToValue).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}

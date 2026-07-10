using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class MessagesStreamResponseAccumulatorTests
{
    [Fact]
    public void BuildResponse_ReconstructsTextAndMergesUsage()
    {
        var accumulator = new MessagesStreamResponseAccumulator(new StreamCaptureBudget(4096));

        accumulator.Accept(Event("message_start", new Dictionary<string, object?>
        {
            ["type"] = "message_start",
            ["message"] = new Dictionary<string, object?>
            {
                ["id"] = "msg_1",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = "claude-sonnet-4-5",
                ["content"] = new List<object?>(),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = 12,
                    ["output_tokens"] = 0,
                    ["cache_read_input_tokens"] = 4
                }
            }
        }));
        accumulator.Accept(Event("content_block_start", new Dictionary<string, object?>
        {
            ["type"] = "content_block_start",
            ["index"] = 0,
            ["content_block"] = new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = string.Empty
            }
        }));
        accumulator.Accept(Delta(0, "text_delta", "text", "Hello"));
        accumulator.Accept(Delta(0, "text_delta", "text", " world"));
        accumulator.Accept(Event("content_block_stop", new Dictionary<string, object?>
        {
            ["type"] = "content_block_stop",
            ["index"] = 0
        }));
        accumulator.Accept(Event("message_delta", new Dictionary<string, object?>
        {
            ["type"] = "message_delta",
            ["delta"] = new Dictionary<string, object?>
            {
                ["stop_reason"] = "end_turn",
                ["stop_sequence"] = "END"
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["output_tokens"] = 2
            }
        }));
        accumulator.Accept(Event("message_stop", new Dictionary<string, object?>
        {
            ["type"] = "message_stop"
        }));

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        Assert.True(accumulator.IsComplete);
        Assert.Equal("msg_1", response["id"]);
        Assert.Equal("message", response["type"]);
        Assert.Equal("assistant", response["role"]);
        Assert.Equal("claude-sonnet-4-5", response["model"]);
        Assert.Equal("end_turn", response["stop_reason"]);
        Assert.Equal("END", response["stop_sequence"]);

        var block = Assert.IsType<Dictionary<string, object?>>(Assert.Single(List(response, "content")));
        Assert.Equal("text", block["type"]);
        Assert.Equal("Hello world", block["text"]);

        var usage = Object(response, "usage");
        Assert.Equal(12, usage["input_tokens"]);
        Assert.Equal(2, usage["output_tokens"]);
        Assert.Equal(4, usage["cache_read_input_tokens"]);
    }

    [Fact]
    public void BuildResponse_ReconstructsThinkingAndSignature()
    {
        var accumulator = StartAccumulator();

        accumulator.Accept(Event("content_block_start", new Dictionary<string, object?>
        {
            ["type"] = "content_block_start",
            ["index"] = 0,
            ["content_block"] = new Dictionary<string, object?>
            {
                ["type"] = "thinking",
                ["thinking"] = string.Empty
            }
        }));
        accumulator.Accept(Delta(0, "thinking_delta", "thinking", "Consider "));
        accumulator.Accept(Delta(0, "thinking_delta", "thinking", "this."));
        accumulator.Accept(Delta(0, "signature_delta", "signature", "signed-value"));
        Stop(accumulator, "end_turn", 3);

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        var block = Assert.IsType<Dictionary<string, object?>>(Assert.Single(List(response, "content")));
        Assert.Equal("thinking", block["type"]);
        Assert.Equal("Consider this.", block["thinking"]);
        Assert.Equal("signed-value", block["signature"]);
    }

    [Fact]
    public void BuildResponse_ParsesFragmentedToolInputJson()
    {
        var accumulator = StartAccumulator();

        accumulator.Accept(Event("content_block_start", new Dictionary<string, object?>
        {
            ["type"] = "content_block_start",
            ["index"] = 0,
            ["content_block"] = new Dictionary<string, object?>
            {
                ["type"] = "tool_use",
                ["id"] = "toolu_1",
                ["name"] = "get_weather",
                ["input"] = new Dictionary<string, object?>()
            }
        }));
        accumulator.Accept(Delta(0, "input_json_delta", "partial_json", "{\"city\":"));
        accumulator.Accept(Delta(0, "input_json_delta", "partial_json", "\"Shanghai\",\"days\":3}"));
        Stop(accumulator, "tool_use", 5);

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        var block = Assert.IsType<Dictionary<string, object?>>(Assert.Single(List(response, "content")));
        Assert.Equal("tool_use", block["type"]);
        Assert.Equal("toolu_1", block["id"]);
        Assert.Equal("get_weather", block["name"]);
        var input = Object(block, "input");
        Assert.Equal("Shanghai", input["city"]);
        Assert.Equal(3, Convert.ToInt32(input["days"]));
    }

    [Fact]
    public void BuildResponse_OrdersMultipleContentBlocksByIndex()
    {
        var accumulator = StartAccumulator();

        accumulator.Accept(BlockStart(2, "text", "text", "last"));
        accumulator.Accept(BlockStart(0, "text", "text", "first"));
        accumulator.Accept(BlockStart(1, "thinking", "thinking", "middle"));
        Stop(accumulator, "end_turn", 3);

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        var content = List(response, "content")
            .Select(item => Assert.IsType<Dictionary<string, object?>>(item))
            .ToList();

        Assert.Equal("first", content[0]["text"]);
        Assert.Equal("middle", content[1]["thinking"]);
        Assert.Equal("last", content[2]["text"]);
    }

    [Fact]
    public void BuildResponse_TruncatesDynamicContentWithinBudget()
    {
        var budget = new StreamCaptureBudget(5);
        var accumulator = StartAccumulator(budget);

        accumulator.Accept(BlockStart(0, "text", "text", string.Empty));
        accumulator.Accept(Delta(0, "text_delta", "text", "abcdef"));
        Stop(accumulator, "end_turn", 1);

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        var block = Assert.IsType<Dictionary<string, object?>>(Assert.Single(List(response, "content")));
        Assert.Equal("abcde", block["text"]);
        Assert.True(budget.Truncated);
    }

    [Fact]
    public void BuildResponse_ReturnsStructuredError()
    {
        var accumulator = new MessagesStreamResponseAccumulator(new StreamCaptureBudget(4096));

        accumulator.Accept(Event("error", new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["request_id"] = "req_1",
            ["error"] = new Dictionary<string, object?>
            {
                ["type"] = "overloaded_error",
                ["message"] = "Please retry later"
            }
        }));

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        Assert.True(accumulator.IsComplete);
        Assert.Equal("error", response["type"]);
        Assert.Equal("req_1", response["request_id"]);
        var error = Object(response, "error");
        Assert.Equal("overloaded_error", error["type"]);
        Assert.Equal("Please retry later", error["message"]);
    }

    [Fact]
    public void BuildResponse_PreservesRedactedThinkingAndCitations()
    {
        var accumulator = StartAccumulator();
        accumulator.Accept(Event("content_block_start", new Dictionary<string, object?>
        {
            ["type"] = "content_block_start",
            ["index"] = 0,
            ["content_block"] = new Dictionary<string, object?>
            {
                ["type"] = "redacted_thinking",
                ["data"] = "encrypted-thinking"
            }
        }));
        accumulator.Accept(Event("content_block_start", new Dictionary<string, object?>
        {
            ["type"] = "content_block_start",
            ["index"] = 1,
            ["content_block"] = new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = "answer"
            }
        }));
        accumulator.Accept(Event("content_block_delta", new Dictionary<string, object?>
        {
            ["type"] = "content_block_delta",
            ["index"] = 1,
            ["delta"] = new Dictionary<string, object?>
            {
                ["type"] = "citations_delta",
                ["citation"] = new Dictionary<string, object?>
                {
                    ["type"] = "char_location",
                    ["cited_text"] = "source"
                }
            }
        }));
        Stop(accumulator, "end_turn", 1);

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        var content = List(response, "content");
        var redacted = Assert.IsType<Dictionary<string, object?>>(content[0]);
        Assert.Equal("redacted_thinking", redacted["type"]);
        Assert.Equal("encrypted-thinking", redacted["data"]);
        var text = Assert.IsType<Dictionary<string, object?>>(content[1]);
        var citations = Assert.IsType<List<object?>>(text["citations"]);
        Assert.Equal("source", Assert.IsType<Dictionary<string, object?>>(Assert.Single(citations))["cited_text"]);
    }

    [Fact]
    public void BuildResponse_DropsRequestEchoAndProviderMetadataFields()
    {
        var accumulator = new MessagesStreamResponseAccumulator(new StreamCaptureBudget(4096));
        accumulator.Accept(Event("message_start", new Dictionary<string, object?>
        {
            ["type"] = "message_start",
            ["message"] = new Dictionary<string, object?>
            {
                ["id"] = "msg_1",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = "claude-sonnet",
                ["instructions"] = "secret instructions",
                ["tools"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "secret_tool" } },
                ["metadata"] = new Dictionary<string, object?> { ["tenant"] = "secret" },
                ["content"] = new List<object?>(),
                ["usage"] = new Dictionary<string, object?> { ["input_tokens"] = 1 }
            }
        }));
        Stop(accumulator, "end_turn", 1);

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());

        Assert.False(response.ContainsKey("instructions"));
        Assert.False(response.ContainsKey("tools"));
        Assert.False(response.ContainsKey("metadata"));
    }

    private static MessagesStreamResponseAccumulator StartAccumulator(StreamCaptureBudget? budget = null)
    {
        var accumulator = new MessagesStreamResponseAccumulator(budget ?? new StreamCaptureBudget(4096));
        accumulator.Accept(Event("message_start", new Dictionary<string, object?>
        {
            ["type"] = "message_start",
            ["message"] = new Dictionary<string, object?>
            {
                ["id"] = "msg_1",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = "claude-sonnet-4-5",
                ["content"] = new List<object?>(),
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = 7,
                    ["output_tokens"] = 0
                }
            }
        }));
        return accumulator;
    }

    private static void Stop(MessagesStreamResponseAccumulator accumulator, string stopReason, int outputTokens)
    {
        accumulator.Accept(Event("message_delta", new Dictionary<string, object?>
        {
            ["type"] = "message_delta",
            ["delta"] = new Dictionary<string, object?>
            {
                ["stop_reason"] = stopReason,
                ["stop_sequence"] = null
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["output_tokens"] = outputTokens
            }
        }));
        accumulator.Accept(Event("message_stop", new Dictionary<string, object?>
        {
            ["type"] = "message_stop"
        }));
    }

    private static SseEvent BlockStart(int index, string type, string field, string value)
    {
        return Event("content_block_start", new Dictionary<string, object?>
        {
            ["type"] = "content_block_start",
            ["index"] = index,
            ["content_block"] = new Dictionary<string, object?>
            {
                ["type"] = type,
                [field] = value
            }
        });
    }

    private static SseEvent Delta(int index, string type, string field, string value)
    {
        return Event("content_block_delta", new Dictionary<string, object?>
        {
            ["type"] = "content_block_delta",
            ["index"] = index,
            ["delta"] = new Dictionary<string, object?>
            {
                ["type"] = type,
                [field] = value
            }
        });
    }

    private static SseEvent Event(string eventName, Dictionary<string, object?> payload)
    {
        return new SseEvent(eventName, payload);
    }

    private static Dictionary<string, object?> Object(
        IReadOnlyDictionary<string, object?> value,
        string key)
    {
        return Assert.IsType<Dictionary<string, object?>>(value[key]);
    }

    private static List<object?> List(IReadOnlyDictionary<string, object?> value, string key)
    {
        return Assert.IsType<List<object?>>(value[key]);
    }
}

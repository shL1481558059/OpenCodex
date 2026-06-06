using System.Text.Json;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Protocols;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.WebSearch;

public sealed class WebSearchSimulatorTests
{
    [Fact]
    public void CanSimulateReadsEnabledStateFromRepository()
    {
        var repository = new FakeProxyWebSearchRepository
        {
            Config = Config(enabled: true)
        };
        var simulator = new WebSearchSimulator(
            new FakeUpstreamClient(),
            new FakeWebSearchClient(),
            repository);

        var result = simulator.CanSimulate(
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "superadmin",
            PayloadWithWebSearchTool());

        Assert.True(result);
        Assert.Equal(1, repository.ReadConfigCallCount);
    }

    [Fact]
    public void CanSimulateReturnsFalseWhenRepositoryConfigIsDisabled()
    {
        var repository = new FakeProxyWebSearchRepository
        {
            Config = Config(enabled: false)
        };
        var simulator = new WebSearchSimulator(
            new FakeUpstreamClient(),
            new FakeWebSearchClient(),
            repository);

        var result = simulator.CanSimulate(
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "superadmin",
            PayloadWithWebSearchTool());

        Assert.False(result);
        Assert.Equal(1, repository.ReadConfigCallCount);
    }

    [Fact]
    public async Task RunAsyncReservesTavilyKeyThroughRepository()
    {
        var key = Key(10, "tvly-test");
        var repository = new FakeProxyWebSearchRepository
        {
            ReservedKey = key
        };
        var upstream = new FakeUpstreamClient();
        upstream.Responses.Enqueue(ChatToolResponse("call_web", "web_search", "{\"query\":\"OpenAI\"}", "gpt-5"));
        upstream.Responses.Enqueue(ChatTextResponse("OpenAI answer", "gpt-5"));
        var webSearch = new FakeWebSearchClient
        {
            Result = new WebSearchProviderResult(
                true,
                200,
                12,
                null,
                null,
                new WebSearchSummary(
                    "OpenAI answer",
                    [
                        new Dictionary<string, object?>
                        {
                            ["title"] = "OpenAI",
                            ["url"] = "https://example.test/openai",
                            ["content"] = "OpenAI answer"
                        }
                    ],
                    null),
                new Dictionary<string, object?> { ["answer"] = "OpenAI answer" })
        };
        var simulator = new WebSearchSimulator(upstream, webSearch, repository);

        var result = await simulator.RunAsync(
            Channel(),
            new Dictionary<string, object?>
            {
                ["model"] = "gpt-5",
                ["messages"] = new List<object?>()
            },
            PayloadWithWebSearchTool(),
            "gpt-5",
            30,
            CancellationToken.None);

        Assert.Equal(1, repository.ReserveKeyCallCount);
        var call = Assert.Single(webSearch.Calls);
        Assert.Equal(key.Provider, call.Key.Provider);
        Assert.Equal(key.Key, call.Key.Key);
        Assert.Equal("OpenAI", call.Query);
        Assert.Collection(
            upstream.Calls,
            _ =>
            {
            },
            second =>
            {
                var messages = Assert.IsType<List<object?>>(second.Payload["messages"]);
                Assert.Collection(
                    messages,
                    assistantItem =>
                    {
                        var assistant = Assert.IsType<Dictionary<string, object?>>(assistantItem);
                        Assert.Equal("assistant", assistant["role"]);
                        var toolCalls = Assert.IsType<List<object?>>(assistant["tool_calls"]);
                        var toolCall = Assert.IsType<Dictionary<string, object?>>(Assert.Single(toolCalls));
                        Assert.Equal("call_web", toolCall["id"]);
                    },
                    toolItem =>
                    {
                        var tool = Assert.IsType<Dictionary<string, object?>>(toolItem);
                        Assert.Equal("tool", tool["role"]);
                        Assert.Equal("call_web", tool["tool_call_id"]);
                        Assert.Contains("OpenAI answer", Assert.IsType<string>(tool["content"]), StringComparison.Ordinal);
                    });
            });
        Assert.Equal("web_search_call", result.ResponsePayload["output"] is List<object?> output
            && output[0] is Dictionary<string, object?> item
                ? item["type"]
                : null);
        var webLog = Assert.Single(result.Details["calls"] as List<object?> ?? []);
        Assert.Equal("OpenAI", webLog is Dictionary<string, object?> log ? log["query"] : null);
        var summaries = Assert.IsType<List<object?>>(result.Details["upstream_call_summary"]);
        Assert.Collection(
            summaries,
            item =>
            {
                var summary = Assert.IsType<Dictionary<string, object?>>(item);
                Assert.Equal(1, summary["iteration"]);
                Assert.Equal(false, summary["after_limit"]);
                Assert.Equal(1, summary["tool_call_count"]);
                Assert.Equal(
                    ["web_search"],
                    Assert.IsType<List<object?>>(summary["tool_names"]).Cast<string>().ToList());
            },
            item =>
            {
                var summary = Assert.IsType<Dictionary<string, object?>>(item);
                Assert.Equal(2, summary["iteration"]);
                Assert.Equal(false, summary["after_limit"]);
                Assert.Equal(0, summary["tool_call_count"]);
                Assert.Empty(Assert.IsType<List<object?>>(summary["tool_names"]));
            });
    }

    [Fact]
    public async Task RunAsyncReplacesOnlyWebSearchFunctionCallsWhenOtherToolCallsRemain()
    {
        var key = Key(10, "tvly-test");
        var repository = new FakeProxyWebSearchRepository
        {
            ReservedKey = key
        };
        var upstream = new FakeUpstreamClient();
        upstream.Responses.Enqueue(ChatToolResponse(
            "gpt-5",
            ("call_web", "web_search", "{\"query\":\"OpenAI\"}"),
            ("call_other", "lookup", "{\"id\":1}")));
        var webSearch = new FakeWebSearchClient
        {
            Result = new WebSearchProviderResult(
                true,
                200,
                12,
                null,
                null,
                new WebSearchSummary(
                    "OpenAI answer",
                    [
                        new Dictionary<string, object?>
                        {
                            ["title"] = "OpenAI",
                            ["url"] = "https://example.test/openai",
                            ["content"] = "OpenAI answer"
                        }
                    ],
                    null),
                new Dictionary<string, object?> { ["answer"] = "OpenAI answer" })
        };
        var simulator = new WebSearchSimulator(upstream, webSearch, repository);

        var result = await simulator.RunAsync(
            Channel(),
            new Dictionary<string, object?>
            {
                ["model"] = "gpt-5",
                ["messages"] = new List<object?>()
            },
            PayloadWithWebSearchTool(),
            "gpt-5",
            30,
            CancellationToken.None);

        Assert.Single(upstream.Calls);
        Assert.Equal(1, repository.ReserveKeyCallCount);
        var output = Assert.IsType<List<object?>>(result.ResponsePayload["output"]);
        Assert.Collection(
            output,
            item =>
            {
                var webSearchItem = Assert.IsType<Dictionary<string, object?>>(item);
                Assert.Equal("web_search_call", webSearchItem["type"]);
                Assert.Equal("call_web", webSearchItem["id"]);
                Assert.True(webSearchItem.ContainsKey("opencodex_result"));
            },
            item =>
            {
                var remainingFunction = Assert.IsType<Dictionary<string, object?>>(item);
                Assert.Equal("function_call", remainingFunction["type"]);
                Assert.Equal("lookup", remainingFunction["name"]);
                Assert.Equal("call_other", remainingFunction["call_id"]);
            });
    }

    [Fact]
    public async Task RunChatStreamAsyncContinuesSequenceAndOutputIndexAfterInjectedWebSearchEvents()
    {
        var key = Key(10, "tvly-test");
        var repository = new FakeProxyWebSearchRepository
        {
            ReservedKey = key
        };
        var upstream = new FakeUpstreamClient();
        upstream.StreamLineBatches.Enqueue(
        [
            "data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call_web\",\"type\":\"function\",\"function\":{\"name\":\"web_search\",\"arguments\":\"{\\\"query\\\":\\\"Open\"}}]},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"AI\\\"}\"}}]},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}\n",
            "\n",
            "data: [DONE]\n",
            "\n"
        ]);
        upstream.StreamLineBatches.Enqueue(
        [
            "data: {\"id\":\"chatcmpl_answer\",\"object\":\"chat.completion.chunk\",\"created\":2,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"answer\"},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_answer\",\"object\":\"chat.completion.chunk\",\"created\":2,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":2,\"total_tokens\":6}}\n",
            "\n",
            "data: [DONE]\n",
            "\n"
        ]);
        var webSearch = new FakeWebSearchClient
        {
            Result = new WebSearchProviderResult(
                true,
                200,
                12,
                null,
                null,
                new WebSearchSummary(
                    "OpenAI answer",
                    [
                        new Dictionary<string, object?>
                        {
                            ["title"] = "OpenAI",
                            ["url"] = "https://example.test/openai",
                            ["content"] = "OpenAI answer"
                        }
                    ],
                    null),
                new Dictionary<string, object?> { ["answer"] = "OpenAI answer" })
        };
        var simulator = new WebSearchSimulator(upstream, webSearch, repository);
        var streamResult = new WebSearchStreamResult();

        var events = new List<(string EventName, JsonElement Payload)>();
        await foreach (var line in simulator.RunChatStreamAsync(
            Channel(),
            new Dictionary<string, object?>
            {
                ["model"] = "gpt-5",
                ["messages"] = new List<object?>()
            },
            PayloadWithWebSearchTool(),
            "gpt-5",
            30,
            streamResult,
            CancellationToken.None))
        {
            events.Add(ParseEvent(line));
        }

        Assert.Equal(2, upstream.Calls.Count);
        Assert.Single(webSearch.Calls);
        Assert.NotNull(streamResult.ResponsePayload);

        var sequenceNumbers = events
            .Select(item => item.Payload.GetProperty("sequence_number").GetInt32())
            .ToList();
        Assert.Equal(Enumerable.Range(0, sequenceNumbers.Count), sequenceNumbers);

        var webAdded = Assert.Single(events, item =>
            item.EventName == "response.output_item.added"
            && item.Payload.GetProperty("item").GetProperty("type").GetString() == "web_search_call");
        Assert.Equal(1, webAdded.Payload.GetProperty("sequence_number").GetInt32());
        Assert.Equal(0, webAdded.Payload.GetProperty("output_index").GetInt32());

        var webDone = Assert.Single(events, item =>
            item.EventName == "response.output_item.done"
            && item.Payload.GetProperty("item").GetProperty("type").GetString() == "web_search_call");
        Assert.Equal(2, webDone.Payload.GetProperty("sequence_number").GetInt32());
        Assert.Equal(0, webDone.Payload.GetProperty("output_index").GetInt32());

        var finalMessageAdded = Assert.Single(events, item =>
            item.EventName == "response.output_item.added"
            && item.Payload.GetProperty("item").GetProperty("type").GetString() == "message");
        Assert.Equal(3, finalMessageAdded.Payload.GetProperty("sequence_number").GetInt32());
        Assert.Equal(1, finalMessageAdded.Payload.GetProperty("output_index").GetInt32());
    }

    private static Dictionary<string, object?> PayloadWithWebSearchTool()
    {
        return new Dictionary<string, object?>
        {
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "web_search"
                }
            }
        };
    }

    private static Dictionary<string, object?> Channel()
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "chat",
            ["type"] = ProtocolConverter.Chat,
            ["baseurl"] = "https://example.test/v1",
            ["apikey"] = "upstream-secret"
        };
    }

    private static WebSearchConfigRecord Config(bool enabled)
    {
        return new WebSearchConfigRecord(
            enabled,
            ["tavily"],
            1000,
            []);
    }

    private static (string EventName, JsonElement Payload) ParseEvent(string line)
    {
        var eventName = "message";
        var dataLines = new List<string>();
        foreach (var rawLine in line.Split('\n'))
        {
            var current = rawLine.TrimEnd('\r');
            if (current.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = current["event:".Length..].Trim();
            }
            else if (current.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(current["data:".Length..].TrimStart());
            }
        }

        using var document = JsonDocument.Parse(string.Join("\n", dataLines));
        return (eventName, document.RootElement.Clone());
    }

    private static TavilyKeyRecord Key(long id, string key)
    {
        return new TavilyKeyRecord(
            id,
            0,
            "tavily",
            key,
            true,
            1,
            1000,
            1000);
    }

    private static Dictionary<string, object?> ChatTextResponse(string text, string model)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_1",
            ["model"] = model,
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = text
                    },
                    ["finish_reason"] = "stop"
                }
            }
        };
    }

    private static Dictionary<string, object?> ChatToolResponse(
        string callId,
        string toolName,
        string arguments,
        string model)
    {
        return ChatToolResponse(model, (callId, toolName, arguments));
    }

    private static Dictionary<string, object?> ChatToolResponse(
        string model,
        params (string CallId, string ToolName, string Arguments)[] toolCalls)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_tool",
            ["model"] = model,
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = string.Empty,
                        ["tool_calls"] = toolCalls
                            .Select(call => (object?)new Dictionary<string, object?>
                            {
                                ["id"] = call.CallId,
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = call.ToolName,
                                    ["arguments"] = call.Arguments
                                }
                            })
                            .ToList()
                    },
                    ["finish_reason"] = "tool_calls"
                }
            }
        };
    }

    private sealed class FakeProxyWebSearchRepository : IProxyWebSearchRepository
    {
        public WebSearchConfigRecord Config { get; init; } = Config(false);

        public TavilyKeyRecord? ReservedKey { get; init; }

        public int ReadConfigCallCount { get; private set; }

        public int ReserveKeyCallCount { get; private set; }

        public WebSearchConfigRecord ReadWebSearchConfig()
        {
            ReadConfigCallCount++;
            return Config;
        }

        public TavilyKeyRecord? ReserveTavilyKey()
        {
            ReserveKeyCallCount++;
            return ReservedKey;
        }
    }

    private sealed class FakeWebSearchClient : IWebSearchClient
    {
        public WebSearchProviderResult Result { get; init; } = new(
            false,
            null,
            0,
            "request_error",
            "failed",
            new WebSearchSummary(string.Empty, [], "failed"),
            null);

        public List<(WebSearchProviderKey Key, string Query)> Calls { get; } = [];

        public Task<WebSearchProviderResult> SearchAsync(
            WebSearchProviderKey key,
            string query,
            CancellationToken cancellationToken)
        {
            Calls.Add((key, query));
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeUpstreamClient : IUpstreamClient
    {
        public Queue<Dictionary<string, object?>> Responses { get; } = [];

        public Queue<IReadOnlyList<string>> StreamLineBatches { get; } = [];

        public List<(IReadOnlyDictionary<string, object?> Channel, IReadOnlyDictionary<string, object?> Payload)> Calls { get; } = [];

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            Calls.Add((channel, payload));
            return Task.FromResult(Responses.Dequeue());
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            Calls.Add((channel, payload));
            var lines = StreamLineBatches.Count > 0
                ? StreamLineBatches.Dequeue()
                : [];
            foreach (var line in lines)
            {
                yield return line;
            }
        }
    }
}

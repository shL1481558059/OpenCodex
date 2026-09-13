using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.Services.WebSearch;
using Xunit;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Api.Tests;

public sealed class BuiltinToolSessionTests
{
    private const string Tool = WebSearchRequestPolicy.InternalToolName;

    [Fact]
    public async Task DeclaredButNotCalled_DoesNotExecuteOrCreateSearchLog()
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var upstream = ChatResponse("Hello");
        var response = Convert(upstream);

        await Drain(session, upstream, response);

        Assert.Equal(0, executor.Calls);
        Assert.Null(session.NextRequest);
        Assert.Null(session.Details);
        Assert.Same(response, session.ResponsePayload);
    }

    [Fact]
    public async Task MixedCalls_ExecutesOnlySearchAndRestoresStrippedHistory()
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var upstream = ChatResponse(null, ("search_1", Tool), ("read_1", "read_file"));

        await Drain(session, upstream, Convert(upstream));

        Assert.Equal(1, executor.Calls);
        Assert.Null(session.NextRequest);
        var items = ListValue(session.ResponsePayload!, "output").Cast<Dictionary<string, object?>>().ToList();
        var search = Assert.Single(items, item => StringValue(item, "type") == "web_search_call");
        var client = Assert.Single(items, item => StringValue(item, "name") == "read_file");
        search.Remove("opencodex_result");
        var next = new Dictionary<string, object?>
        {
            ["input"] = new List<object?>
            {
                search, client,
                new Dictionary<string, object?>
                {
                    ["type"] = "function_call_output", ["call_id"] = "read_1", ["output"] = "file contents"
                }
            }
        };
        await store.RestoreAsync(next, WebSearchTestStore.OwnerUserId, Tool, CancellationToken.None);

        var history = ListValue(next, "input").Cast<Dictionary<string, object?>>().ToList();
        Assert.Equal(new[] { "function_call", "function_call", "function_call_output", "function_call_output" },
            history.Select(item => StringValue(item, "type")));
        Assert.Equal(Tool, history[0]["name"]);
        Assert.Contains("source", StringValue(history[2], "output"), StringComparison.Ordinal);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task MultipleRounds_PreserveAllOutputAndSumUsage()
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var first = ChatResponse("Before.", ("search_1", Tool));
        await Drain(session, first, Convert(first));
        Assert.NotNull(session.NextRequest);
        Assert.False(session.NextRequest.ContainsKey("tool_choice"));
        Assert.Contains(ListValue(session.NextRequest, "messages").Cast<Dictionary<string, object?>>(),
            item => StringValue(item, "role") == "tool");
        var second = ChatResponse("After.");
        await Drain(session, second, Convert(second));

        Assert.Null(session.NextRequest);
        Assert.Equal(20L, session.Usage["input_tokens"]);
        Assert.Equal(4L, session.Usage["output_tokens"]);
        var serialized = JsonDumps(session.ResponsePayload);
        Assert.Contains("Before.", serialized, StringComparison.Ordinal);
        Assert.Contains("After.", serialized, StringComparison.Ordinal);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task DuplicateCall_DoesNotExecuteOrEmitLifecycleTwice()
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var upstream = ChatResponse(null, ("same", Tool), ("same", Tool));
        var progress = await Drain(session, upstream, Convert(upstream));
        Assert.Equal(1, executor.Calls);
        Assert.Equal(2, progress.Count);
        Assert.Single(session.Results);
    }

    [Fact]
    public async Task MissingOrForeignContinuation_IsRestoredAsUnavailableWithoutSearching()
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var upstream = ChatResponse(null, ("search_1", Tool), ("read_1", "read_file"));
        await Drain(session, upstream, Convert(upstream));
        var item = DeepCopyObject(Assert.Single(ListValue(session.ResponsePayload!, "output")
            .Cast<Dictionary<string, object?>>(), value => StringValue(value, "type") == "web_search_call"));
        item.Remove("opencodex_result");
        var payload = new Dictionary<string, object?> { ["input"] = new List<object?> { item } };

        await store.RestoreAsync(payload, Guid.NewGuid(), Tool, CancellationToken.None);

        var restored = ListValue(payload, "input").Cast<Dictionary<string, object?>>()
            .Single(value => StringValue(value, "type") == "function_call_output");
        Assert.Contains("unavailable", StringValue(restored, "output"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task RepeatedCallIds_StillConsumeIterationBudget()
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store, 2);
        for (var round = 0; round < 5; round++)
        {
            var upstream = ChatResponse(null, ("same", Tool));
            await Drain(session, upstream, Convert(upstream));
        }
        Assert.Equal(1, executor.Calls);
        Assert.NotNull(session.NextRequest);
        Assert.Empty(ListValue(session.NextRequest, "tools"));
    }

    [Fact]
    public async Task ProviderFailure_DisablesRemainingCallsInSameBatch()
    {
        var executor = new CountingExecutor { Fail = true };
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var upstream = ChatResponse(null, ("first", Tool), ("second", Tool));

        await Drain(session, upstream, Convert(upstream));

        Assert.Equal(1, executor.Calls);
        Assert.Equal(2, session.Results.Count);
        Assert.NotNull(session.NextRequest);
        Assert.Empty(ListValue(session.NextRequest, "tools"));
    }

    [Fact]
    public async Task CancelledExecution_RemainsVisibleInDiagnosticLog()
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new CountingExecutor { CancelSource = cancellation };
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var upstream = ChatResponse(null, ("first", Tool));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Drain(session, upstream, Convert(upstream), cancellation.Token));

        Assert.True(session.Binding.HasExecuted);
        Assert.NotNull(session.Details);
        Assert.Equal("failed", Assert.Single(session.Results).Status);
    }

    [Fact]
    public async Task UnfinishedToolCall_IsRejectedBeforeExecution()
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var upstream = ChatResponse(null, ("first", Tool));
        ((Dictionary<string, object?>)ListValue(upstream, "choices")[0]!).Remove("finish_reason");

        await Assert.ThrowsAsync<UpstreamException>(() => Drain(session, upstream, Convert(upstream)));

        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task ExhaustedOutputBudget_DoesNotExecuteSearch()
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = new BuiltinToolSession(
            new BuiltinToolRequestContext
            {
                OwnerUserId = WebSearchTestStore.OwnerUserId,
                WebSearchToolName = Tool
            },
            executor,
            store,
            "chat",
            120,
            1);
        var upstream = ChatResponse(null, ("first", Tool));

        await Drain(session, upstream, Convert(upstream));

        Assert.Equal(0, executor.Calls);
        Assert.Null(session.NextRequest);
        Assert.Equal("incomplete", session.ResponsePayload!["status"]);
    }

    public static TheoryData<object> IntegralUsageValues => new()
    {
        42, 42L, 42d, 42m
    };

    [Theory]
    [MemberData(nameof(IntegralUsageValues))]
    public async Task AccountingUsage_AcceptsIntegralNumericRepresentations(object value)
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var upstream = ChatResponse(null, ("search_1", Tool));
        var response = Convert(upstream);
        ObjectValue(upstream, "usage")["prompt_tokens"] = value;

        await Drain(session, upstream, response);

        Assert.Equal(42L, session.AccountingUsage["prompt_tokens"]);
    }

    [Fact]
    public async Task AccountingUsage_AccumulatesNestedDecodedCounters()
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var first = ChatResponse(null, ("search_1", Tool));
        var second = ChatResponse("answer");
        foreach (var upstream in new[] { first, second })
        {
            upstream["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 10d,
                ["completion_tokens"] = 2d,
                ["prompt_tokens_details"] = new Dictionary<string, object?> { ["cached_tokens"] = 3d },
                ["completion_tokens_details"] = new Dictionary<string, object?> { ["reasoning_tokens"] = 1m },
                ["vendor_latency_seconds"] = 0.125d
            };
            await Drain(session, upstream, Convert(upstream));
        }

        Assert.Equal(20L, session.AccountingUsage["prompt_tokens"]);
        Assert.Equal(4L, session.AccountingUsage["completion_tokens"]);
        Assert.Equal(6L, ObjectValue(session.AccountingUsage, "prompt_tokens_details")["cached_tokens"]);
        Assert.Equal(2L, ObjectValue(session.AccountingUsage, "completion_tokens_details")["reasoning_tokens"]);
        Assert.False(session.AccountingUsage.ContainsKey("vendor_latency_seconds"));
    }

    public static TheoryData<object> InvalidUsageValues => new()
    {
        -1, -1L, -1d, 1.5d, 1.5m, double.NaN, double.PositiveInfinity,
        9_223_372_036_854_775_808d, "42", true
    };

    [Theory]
    [MemberData(nameof(InvalidUsageValues))]
    public async Task AccountingUsage_RejectsInvalidTokenCounters(object value)
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var upstream = ChatResponse(null, ("search_1", Tool));
        var response = Convert(upstream);
        ObjectValue(upstream, "usage")["prompt_tokens"] = value;

        await Assert.ThrowsAsync<UpstreamException>(() => Drain(session, upstream, response));

        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task AccountingUsage_RejectsAccumulationOverflowBeforeAnotherSearch()
    {
        var executor = new CountingExecutor();
        var store = WebSearchTestStore.Create();
        using var session = Session(executor, store);
        var first = ChatResponse(null, ("search_1", Tool));
        var firstResponse = Convert(first);
        ObjectValue(first, "usage")["prompt_tokens"] = long.MaxValue;
        await Drain(session, first, firstResponse);
        var second = ChatResponse(null, ("search_2", Tool));
        var secondResponse = Convert(second);
        ObjectValue(second, "usage")["prompt_tokens"] = 1L;

        var exception = await Assert.ThrowsAsync<UpstreamException>(() => Drain(session, second, secondResponse));

        Assert.Contains("exceeds the supported range", exception.Message);
        Assert.Equal(long.MaxValue, session.AccountingUsage["prompt_tokens"]);
        Assert.Equal(1, executor.Calls);
    }

    private static BuiltinToolSession Session(CountingExecutor executor, WebSearchContinuationStore store, int maxCalls = 15) =>
        new(new BuiltinToolRequestContext
            {
                OwnerUserId = WebSearchTestStore.OwnerUserId,
                WebSearchToolName = Tool,
                MaxWebSearchCalls = maxCalls
            },
            executor,
            store,
            "chat",
            120,
            null);

    private static async Task<List<BuiltinToolProgress>> Drain(
        BuiltinToolSession session, Dictionary<string, object?> upstream, Dictionary<string, object?> response,
        CancellationToken cancellationToken = default)
    {
        var progress = new List<BuiltinToolProgress>();
        var request = new Dictionary<string, object?>
        {
            ["messages"] = new List<object?>(),
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?> { ["name"] = Tool }
                }
            },
            ["tool_choice"] = "required"
        };
        await foreach (var item in session.ProcessRoundAsync(request, upstream, response, cancellationToken))
        {
            progress.Add(item);
        }
        return progress;
    }

    private static Dictionary<string, object?> Convert(Dictionary<string, object?> upstream) =>
        ProtocolConverter.ConvertResponse(upstream, "responses", "chat", "public");

    private static Dictionary<string, object?> ChatResponse(string? text, params (string Id, string Name)[] calls) => new()
    {
        ["id"] = "chat_1",
        ["model"] = "model",
        ["choices"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["index"] = 0,
                ["finish_reason"] = calls.Length > 0 ? "tool_calls" : "stop",
                ["message"] = new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = text,
                    ["tool_calls"] = calls.Select(call => (object?)new Dictionary<string, object?>
                    {
                        ["id"] = call.Id, ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = call.Name, ["arguments"] = "{\"query\":\"test\"}"
                        }
                    }).ToList()
                }
            }
        },
        ["usage"] = new Dictionary<string, object?> { ["prompt_tokens"] = 10, ["completion_tokens"] = 2, ["total_tokens"] = 12 }
    };

    private sealed class CountingExecutor : IWebSearchToolExecutor
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public CancellationTokenSource? CancelSource { get; init; }
        public string CurrentMode() => WebSearchModes.Simulate;
        public Task<WebSearchToolResult> ExecuteAsync(string callId, string arguments, CancellationToken cancellationToken)
        {
            Calls++;
            CancelSource?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail)
            {
                return Task.FromResult(WebSearchToolResult.Failed(callId, "test", "provider unavailable", true));
            }
            var result = new Dictionary<string, object?>
            {
                ["answer"] = "source answer", ["results"] = new List<object?>()
            };
            return Task.FromResult(new WebSearchToolResult(
                callId, "test", "completed", JsonDumps(result), result,
                null, "test", null, null, null, null, null, 200, null));
        }
    }
}

using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class NativeClientResponseExecutorTests
{
    [Fact]
    public async Task ToolDoneWithoutAddedFailsWithoutPublishingDuplicateCompletion()
    {
        var (store, session, executor) = await Setup(); var request = Request(); var events = new List<D>();
        var result = await executor.ExecuteAsync(session, request, "turn", async (_, emit, ct) =>
        {
            await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = Tool("one") }, ct);
            return Response("response", Tool("one"));
        }, e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.Equal("failed", result["status"]);
        Assert.DoesNotContain(events, e => Type(e) is "response.output_item.done" or "response.completed");
        Assert.Single(events, e => Type(e) == "response.failed");
        Assert.Null((await store.ProjectRequestAsync(session, request, "turn", default)).ReplayResponse);
    }

    [Theory]
    [InlineData("message_to_custom")]
    [InlineData("search_server_to_client")]
    [InlineData("search_missing_execution")]
    public async Task EveryStreamedItemMustMatchTheFinalToolIdentity(string scenario)
    {
        var (store, session, executor) = await Setup(); var request = SearchRequest(); var events = new List<D>();
        var complete = scenario == "message_to_custom" ? Tool("one") : SearchCall();
        var streamed = scenario == "message_to_custom" ? Message("placeholder") : WebSearchPayload.DeepCopyObject(complete);
        streamed["id"] = complete["id"];
        if (scenario == "search_server_to_client") streamed["execution"] = "server";
        if (scenario == "search_missing_execution") streamed.Remove("execution");
        var result = await executor.ExecuteAsync(session, request, "turn", async (_, emit, ct) =>
        {
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = streamed }, ct);
            await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = streamed }, ct);
            return Response("response", complete);
        }, e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.Equal("failed", result["status"]);
        Assert.DoesNotContain(events, e => Type(e) is "response.output_item.done" or "response.completed");
        if (scenario != "message_to_custom") Assert.DoesNotContain(events, e => Type(e) == "response.output_item.added");
        Assert.Null((await store.ProjectRequestAsync(session, request, "turn", default)).ReplayResponse);
    }

    [Theory]
    [InlineData("custom_tool_call", "const value = 'a b';\ntext(value);")]
    [InlineData("custom_tool_call", " \nconst value = 'a b';\ntext(value); \r\n")]
    [InlineData("function_call", "{\"query\":\"a b\"}")]
    [InlineData("function_call", " \n{\"query\":\"a b\"}\r\n ")]
    public async Task ToolWirePayloadAndEveryWhitespaceFragmentRemainExactAcrossCommitAndReplay(string type, string payload)
    {
        var (_, session, executor) = await Setup();
        var request = Request();
        ((D)JsonDictionaryValue.List(request, "tools")[0]!)["type"] = type == "function_call" ? "function" : "custom";
        var field = type == "function_call" ? "arguments" : "input";
        var prefix = type == "function_call" ? "response.function_call_arguments." : "response.custom_tool_call_input.";
        var call = new D { ["id"] = "item-wire", ["call_id"] = "wire-call", ["type"] = type, ["name"] = "exec", [field] = payload };
        var modelCalls = 0;
        async Task<D> Model(D _, Func<D, CancellationToken, Task> emit, CancellationToken ct)
        {
            modelCalls++;
            var started = WebSearchPayload.DeepCopyObject(call); started[field] = "";
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = started }, ct);
            foreach (var character in payload)
                await emit(new() { ["type"] = prefix + "delta", ["output_index"] = 0, ["item_id"] = "item-wire", ["delta"] = character.ToString() }, ct);
            await emit(new() { ["type"] = prefix + "done", ["output_index"] = 0, ["item_id"] = "item-wire", [field] = payload }, ct);
            await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = call }, ct);
            return Response("wire-response", call);
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var events = new List<D>();
            var result = await executor.ExecuteAsync(session, request, "turn", Model, e => { events.Add(e); return Task.CompletedTask; }, default);
            Assert.Equal("completed", result["status"]);
            Assert.Equal(payload, ((D)Assert.Single(JsonDictionaryValue.List(result, "output"))!)[field]);
            Assert.Equal(payload, string.Concat(events.Where(e => Type(e) == prefix + "delta").Select(e => (string)e["delta"]!)));
            Assert.Equal(payload, Assert.Single(events, e => Type(e) == prefix + "done")[field]);
            Assert.Single(events, e => Type(e) == "response.completed");
        }
        Assert.Equal(1, modelCalls);
    }

    [Fact]
    public async Task MessageWireTextPreservesWhitespaceOnInitialPublicationAndReplay()
    {
        var (_, session, executor) = await Setup();
        const string text = " \n  indented answer\n\nsecond line \r\n";
        var modelCalls = 0;
        Task<D> Model(D _, Func<D, CancellationToken, Task> __, CancellationToken ___)
        { modelCalls++; return Task.FromResult(Response("message-wire", Message(text))); }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var events = new List<D>();
            await executor.ExecuteAsync(session, Request(), "turn", Model, e => { events.Add(e); return Task.CompletedTask; }, default);
            Assert.Equal(text, Assert.Single(events, e => Type(e) == "response.output_text.delta")["delta"]);
            Assert.Equal(text, Assert.Single(events, e => Type(e) == "response.output_text.done")["text"]);
        }
        Assert.Equal(1, modelCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientToolSearchBatchIsCommittedBeforePublicationWithObjectArguments(bool objectDone)
    {
        var (store, session, executor) = await Setup();
        var request = SearchRequest(); var events = new List<D>();
        var result = await executor.ExecuteAsync(session, request, "search-turn", async (_, emit, ct) =>
        {
            var started = SearchCall(); started["arguments"] = "";
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = started }, ct);
            await emit(new() { ["type"] = "response.function_call_arguments.delta", ["output_index"] = 0,
                ["item_id"] = "search-item", ["delta"] = "{\"query\":\"browser\"}" }, ct);
            await emit(new() { ["type"] = "response.function_call_arguments.done", ["output_index"] = 0,
                ["item_id"] = "search-item", ["arguments"] = objectDone ? new D { ["query"] = "browser" } : "{\"query\":\"browser\"}" }, ct);
            await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = SearchCall() }, ct);
            Assert.Empty(events);
            return Response("search-response", SearchCall(), Tool("sibling"));
        }, async e =>
        {
            if (Type(e) == "response.output_item.added")
            {
                var saved = await store.ProjectRequestAsync(session, request, "search-turn", default);
                Assert.Equal(2, JsonDictionaryValue.List(saved.ReplayResponse!, "output").Count);
            }
            events.Add(e);
        }, default);
        Assert.Equal("completed", result["status"]);
        Assert.Equal("browser", ((D)((D)JsonDictionaryValue.List(result, "output")[0]!)["arguments"]!)["query"]);
        Assert.Single(events, e => Type(e) == "response.completed");
    }

    [Fact]
    public async Task ClientToolSearchOnlyResponseAndReplayRetainCallIdentity()
    {
        var (_, session, executor) = await Setup(); var request = SearchRequest(); var calls = 0;
        Task<D> Model(D _, Func<D, CancellationToken, Task> __, CancellationToken ___)
        { calls++; return Task.FromResult(Response("search-response", SearchCall())); }
        var first = await executor.ExecuteAsync(session, request, "turn", Model, _ => Task.CompletedTask, default);
        var events = new List<D>();
        var replay = await executor.ExecuteAsync(session, request, "turn", Model, e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.Equal("completed", first["status"]);
        Assert.Equal(1, calls);
        Assert.Equal(WebSearchPayload.JsonDumps(first), WebSearchPayload.JsonDumps(replay));
        var done = Assert.Single(events, e => Type(e) == "response.output_item.done");
        Assert.Equal("search-call", ((D)done["item"]!)["call_id"]);
        Assert.Contains(events, e => Type(e) == "response.function_call_arguments.done");
        Assert.DoesNotContain(events, e => Type(e).StartsWith("response.custom_tool_call_input", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientToolSearchChangedArgumentsRejectEntireBatch()
    {
        var (store, session, executor) = await Setup(); var request = SearchRequest(); var events = new List<D>();
        var response = await executor.ExecuteAsync(session, request, "turn", async (_, emit, ct) =>
        {
            var invalid = SearchCall(); invalid["arguments"] = new D { ["query"] = "different" };
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = invalid }, ct);
            await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = invalid }, ct);
            return Response("search-response", SearchCall(), Tool("sibling"));
        }, e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.Equal("failed", response["status"]);
        Assert.DoesNotContain(events, e => Type(e) is "response.output_item.added" or "response.output_item.done" or "response.completed");
        Assert.Null((await store.ProjectRequestAsync(session, request, "turn", default)).ReplayResponse);
    }

    [Fact]
    public async Task IncompleteClientToolSearchNeverEntersContinuationHistory()
    {
        var (store, session, executor) = await Setup(); var request = SearchRequest();
        var partial = Response("partial-search", SearchCall()); partial["status"] = "incomplete";
        partial["incomplete_details"] = new D { ["reason"] = "max_output_tokens" };
        await executor.ExecuteAsync(session, request, "turn", (_, _, _) => Task.FromResult(partial), _ => Task.CompletedTask, default);
        var next = await store.ProjectRequestAsync(session, new() { ["previous_response_id"] = "partial-search", ["input"] = "continue" }, "next", default);
        Assert.DoesNotContain(JsonDictionaryValue.List(next.Payload, "input").OfType<D>(), i => Type(i) == "tool_search_call");
    }

    private static D SearchRequest()
    {
        var request = Request();
        JsonDictionaryValue.List(request, "tools").Add(new D { ["type"] = "tool_search", ["execution"] = "client" });
        return request;
    }
    private static D SearchCall() => new()
    {
        ["id"] = "search-item", ["type"] = "tool_search_call", ["status"] = "completed", ["name"] = "tool_search",
        ["call_id"] = "search-call", ["execution"] = "client", ["arguments"] = new D { ["query"] = "browser" }
    };

    [Fact]
    public async Task CommentaryReturnsAfterOneModelCallAndFullClientInputIsAuthoritative()
    {
        var (store, session, executor) = await Setup();
        var calls = 0;
        var input = new List<object?> { Message("old question", "user"), Message("old answer"), Message("current question", "user"), Message("# AGENTS.md rules", "user") };
        var request = new D { ["model"] = "test", ["input"] = input };
        var events = new List<D>();
        var response = await executor.ExecuteAsync(session, request, "turn", (payload, _, _) =>
        {
            calls++;
            Assert.Equal(WebSearchPayload.JsonDumps(input), WebSearchPayload.JsonDumps(payload["input"]));
            var commentary = Message("still working"); commentary["phase"] = "commentary";
            return Task.FromResult(Response("r1", commentary));
        }, e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.Equal(1, calls);
        Assert.Equal("commentary", ((D)Assert.Single(JsonDictionaryValue.List(response, "output"))!)["phase"]);
        Assert.Single(events, e => Type(e) == "response.completed");
    }

    [Fact]
    public async Task TextStreamsBeforeCompletionAndEntireToolBatchIsCommittedBeforePublication()
    {
        var (store, session, executor) = await Setup();
        var request = Request(); var events = new List<D>();
        await executor.ExecuteAsync(session, request, "turn", async (_, emit, ct) =>
        {
            await emit(new() { ["type"] = "response.created", ["response"] = new D { ["id"] = "r1", ["status"] = "in_progress" } }, ct);
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = new D { ["type"] = "message", ["role"] = "assistant", ["content"] = new List<object?>() } }, ct);
            await emit(new() { ["type"] = "response.output_text.delta", ["output_index"] = 0, ["delta"] = "working" }, ct);
            Assert.Contains(events, e => Type(e) == "response.output_text.delta");
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 1, ["item"] = Tool("one") }, ct);
            await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 1, ["item"] = Tool("one") }, ct);
            Assert.DoesNotContain(events, e => e.TryGetValue("item", out var i) && i is D item && Type(item) == "custom_tool_call");
            return Response("r1", Message("working"), Tool("one"), Tool("two"));
        }, async e =>
        {
            if (e.TryGetValue("item", out var item) && item is D call && Type(call) == "custom_tool_call")
            {
                var replay = await store.ProjectRequestAsync(session, request, "turn", default);
                Assert.Equal(3, JsonDictionaryValue.List(replay.ReplayResponse!, "output").Count);
            }
            events.Add(e);
        }, default);
        Assert.Single(events, e => Type(e) == "response.completed");
        Assert.Equal(Enumerable.Range(1, events.Count), events.Select(e => WebSearchPayload.ToInt(e["sequence_number"], 0)));
    }

    [Fact]
    public async Task InvalidToolBatchEmitsNoExecutableItemsAndOnlyOneFailure()
    {
        var (store, session, executor) = await Setup();
        var request = Request(); var events = new List<D>();
        var result = await executor.ExecuteAsync(session, request, "turn", async (_, emit, ct) =>
        {
            await emit(new() { ["type"] = "response.created", ["response"] = new D { ["id"] = "r1" } }, ct);
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = Tool("one") }, ct);
            await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = Tool("one") }, ct);
            var invalid = Tool("two"); invalid["type"] = "function_call"; invalid.Remove("input"); invalid["arguments"] = "{}";
            return Response("r1", Tool("one"), invalid);
        }, e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.Equal("failed", result["status"]);
        Assert.Single(events, e => Type(e) == "response.failed");
        Assert.DoesNotContain(events, e => Type(e) == "response.output_item.done" || Type(e) == "response.completed");
        Assert.Null((await store.ProjectRequestAsync(session, request, "turn", default)).ReplayResponse);
    }

    [Fact]
    public async Task DisconnectAfterCommitReplaysSameResponseAndToolIdsWithoutModelRetry()
    {
        var (store, session, executor) = await Setup();
        var request = Request(); var calls = 0;
        using var stop = new CancellationTokenSource();
        Task<D> Model(D _, Func<D, CancellationToken, Task> __, CancellationToken ___) { calls++; return Task.FromResult(Response("r1", Tool("one"), Tool("two"))); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(session, request, "turn", Model, e =>
        {
            if (Type(e) == "response.output_item.done") { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); }
            return Task.CompletedTask;
        }, stop.Token));
        var events = new List<D>();
        var replayed = await executor.ExecuteAsync(session, request, "turn", Model, e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.Equal(1, calls);
        Assert.Equal("r1", replayed["id"]);
        Assert.Equal(new[] { "one", "two" }, JsonDictionaryValue.List(replayed, "output").OfType<D>().Select(i => (string)i["call_id"]!));
        Assert.Single(events, e => Type(e) == "response.completed");
    }

    [Fact]
    public async Task EmptyCompletedResponseFailsButLegitimateIncompletePreservesDetails()
    {
        var (_, session, executor) = await Setup();
        var empty = await executor.ExecuteAsync(session, Request(), "empty", (_, _, _) => Task.FromResult(Response("empty")), _ => Task.CompletedTask, default);
        Assert.Equal("failed", empty["status"]);
        var incomplete = new D { ["id"] = "partial", ["status"] = "incomplete", ["output"] = new List<object?> { Message("partial text") }, ["incomplete_details"] = new D { ["reason"] = "max_output_tokens" } };
        var events = new List<D>();
        var partial = await executor.ExecuteAsync(session, Request(), "partial", (_, _, _) => Task.FromResult(incomplete), e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.Equal("incomplete", partial["status"]);
        Assert.Equal("max_output_tokens", ((D)partial["incomplete_details"]!)["reason"]);
        Assert.Single(events, e => Type(e) == "response.incomplete");
        Assert.DoesNotContain(events, e => Type(e) == "response.completed");
    }

    [Fact]
    public async Task UpstreamFailureIsNotCommittedAndNextRequestReallyCallsModel()
    {
        var (_, session, executor) = await Setup();
        var calls = 0;
        Task<D> Model(D _, Func<D, CancellationToken, Task> __, CancellationToken ___)
        {
            calls++;
            return Task.FromResult(calls == 1 ? new D { ["id"] = "failed-id", ["status"] = "failed", ["output"] = new List<object?>(), ["error"] = new D { ["code"] = "overloaded", ["message"] = "retry later" } } : Response("ok-id", Message("answer")));
        }
        var failed = await executor.ExecuteAsync(session, Request(), "turn", Model, _ => Task.CompletedTask, default);
        Assert.Equal("overloaded", ((D)failed["error"]!)["code"]);
        var retried = await executor.ExecuteAsync(session, Request(), "turn", Model, _ => Task.CompletedTask, default);
        Assert.Equal(2, calls);
        Assert.Equal("ok-id", retried["id"]);
    }

    [Fact]
    public async Task ValidTerminalResponseCannotAuthorizeDifferentBufferedToolPayload()
    {
        var (store, session, executor) = await Setup();
        var request = Request(); var events = new List<D>();
        var failed = await executor.ExecuteAsync(session, request, "turn", async (_, emit, ct) =>
        {
            var invalid = Tool("one"); invalid["input"] = "unexpected side effect";
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = invalid }, ct);
            await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = invalid }, ct);
            return Response("r1", Tool("one"));
        }, e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.Equal("failed", failed["status"]);
        Assert.DoesNotContain(events, e => Type(e) == "response.output_item.added" || Type(e) == "response.output_item.done");
        Assert.Null((await store.ProjectRequestAsync(session, request, "turn", default)).ReplayResponse);
    }

    [Fact]
    public async Task ModelCompletingAfterCancellationCannotCommitOrReplaceTheNextAttempt()
    {
        var (store, session, executor) = await Setup();
        var request = Request();
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(session, request, "turn", (_, _, _) =>
        {
            stop.Cancel();
            return Task.FromResult(Response("late-response", Tool("one")));
        }, _ => Task.CompletedTask, stop.Token));
        Assert.Null((await store.ProjectRequestAsync(session, request, "turn", default)).ReplayResponse);
        var calls = 0;
        var next = await executor.ExecuteAsync(session, request, "turn", (_, _, _) =>
        {
            calls++;
            return Task.FromResult(Response("fresh-response", Message("answer")));
        }, _ => Task.CompletedTask, default);
        Assert.Equal(1, calls);
        Assert.Equal("fresh-response", next["id"]);
    }

    [Theory]
    [InlineData("response.custom_tool_call_input.delta")]
    [InlineData("response.custom_tool_call_input.done")]
    public async Task ToolArgumentEventCannotTargetAnotherItemWithTheSameOutputIndex(string eventType)
    {
        var (store, session, executor) = await Setup();
        var request = Request(); var events = new List<D>();
        var failed = await executor.ExecuteAsync(session, request, "turn", async (_, emit, ct) =>
        {
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = Tool("one") }, ct);
            await emit(new() { ["type"] = eventType, ["output_index"] = 0, ["item_id"] = "different-item", [eventType.EndsWith("delta") ? "delta" : "input"] = "run once" }, ct);
            return Response("r1", Tool("one"));
        }, e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.Equal("failed", failed["status"]);
        Assert.DoesNotContain(events, e => Type(e).StartsWith("response.custom_tool_call_input") || Type(e) == "response.output_item.done");
        Assert.Null((await store.ProjectRequestAsync(session, request, "turn", default)).ReplayResponse);
    }

    [Theory]
    [InlineData("response.output_item.added")]
    [InlineData("response.output_item.done")]
    public async Task DuplicateToolItemEventsAreRejectedBeforeAnyToolBecomesExecutable(string duplicateType)
    {
        var (store, session, executor) = await Setup();
        var request = Request(); var events = new List<D>();
        var failed = await executor.ExecuteAsync(session, request, "turn", async (_, emit, ct) =>
        {
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = Tool("one") }, ct);
            await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = Tool("one") }, ct);
            await emit(new() { ["type"] = duplicateType, ["output_index"] = 0, ["item"] = Tool("one") }, ct);
            return Response("r1", Tool("one"));
        }, e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.Equal("failed", failed["status"]);
        Assert.DoesNotContain(events, e => Type(e) == "response.output_item.added" || Type(e) == "response.output_item.done");
        Assert.Null((await store.ProjectRequestAsync(session, request, "turn", default)).ReplayResponse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteResponseCanContinueWithoutReplayingOrExecutingPartialTools(bool persist)
    {
        var directory = Path.Combine(Path.GetTempPath(), "native-incomplete-" + Guid.NewGuid().ToString("N"));
        var owner = Guid.NewGuid();
        var identity = new MultiAgentClientIdentity { ThreadId = "root", RootThreadId = "root", AgentName = "/root" };
        var store = new NativeClientSessionStore(directory);
        var session = await store.ResolveAsync(owner, identity, persist, 1, default);
        var request = Request();
        var partial = Response("partial", Message("partial answer"), Tool("unfinished"));
        partial["status"] = "incomplete";
        partial["incomplete_details"] = new D { ["reason"] = "max_output_tokens" };
        var events = new List<D>();
        await new NativeClientResponseExecutor(store).ExecuteAsync(session, request, "turn",
            (_, _, _) => Task.FromResult(partial), e => { events.Add(e); return Task.CompletedTask; }, default);
        Assert.DoesNotContain(events, e => Type(e) == "response.output_item.done");
        if (persist)
        {
            store = new NativeClientSessionStore(directory);
            session = await store.ResolveAsync(owner, identity, persist, 1, default);
        }
        Assert.Null((await store.ProjectRequestAsync(session, request, "turn", default)).ReplayResponse);
        var continuation = await store.ProjectRequestAsync(session,
            new D { ["model"] = "test", ["previous_response_id"] = "partial", ["input"] = "continue" }, "next", default);
        var input = JsonDictionaryValue.List(continuation.Payload, "input").OfType<D>().ToList();
        Assert.Equal(3, input.Count);
        Assert.Contains("partial answer", WebSearchPayload.JsonDumps(input));
        Assert.DoesNotContain(input, i => Type(i) is "function_call" or "custom_tool_call");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static async Task<(NativeClientSessionStore, NativeClientSession, NativeClientResponseExecutor)> Setup()
    {
        var store = new NativeClientSessionStore(Path.Combine(Path.GetTempPath(), "native-runner-" + Guid.NewGuid().ToString("N")));
        var session = await store.ResolveAsync(Guid.NewGuid(), new() { ThreadId = "root", RootThreadId = "root", AgentName = "/root" }, false, 1, default);
        return (store, session, new(store));
    }
    private static D Request() => new() { ["model"] = "test", ["input"] = "question", ["tools"] = new List<object?> { new D { ["type"] = "custom", ["name"] = "exec" } } };
    private static string Type(D value) => JsonDictionaryValue.String(value, "type");
    private static D Tool(string id) => new() { ["id"] = "item-" + id, ["type"] = "custom_tool_call", ["name"] = "exec", ["call_id"] = id, ["input"] = "run once" };
    private static D Message(string text, string role = "assistant") => new() { ["type"] = "message", ["role"] = role, ["content"] = new List<object?> { new D { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = text } } };
    private static D Response(string id, params D[] items) => new() { ["id"] = id, ["object"] = "response", ["status"] = "completed", ["output"] = items.Cast<object?>().ToList() };
}

using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class NativeClientSessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ocxp-native-session-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _owner = Guid.NewGuid();

    [Fact]
    public async Task FullClientHistoryReplacesPreviousHistoryIncludingForkAndCompactionWrappers()
    {
        var store = Store(); var session = await Resolve(store);
        var first = await store.ProjectRequestAsync(session, Request("OLD_ANSWERED_QUESTION"), "turn-one", default);
        await store.CommitResponseAsync(session, first, Response("response-one", "old answer"), default);
        var input = new List<object?> { Message("CURRENT_QUESTION"), Message("<environment_context>date</environment_context>"), Message("# AGENTS.md rules"), Message("Another language model started to solve this problem: CURRENT_QUESTION") };
        var request = new D { ["model"] = "test", ["input"] = input };
        var current = await store.ProjectRequestAsync(session, request, "turn-two", default);
        Assert.Equal(WebSearchPayload.JsonDumps(input), WebSearchPayload.JsonDumps(current.Payload["input"]));
        Assert.DoesNotContain("OLD_ANSWERED_QUESTION", WebSearchPayload.JsonDumps(current.Payload));
        Assert.Null(current.ReplayResponse);
    }

    [Fact]
    public async Task PreviousResponseAppendsOnlyInputAndOutputAndInheritsOmittedSettings()
    {
        var store = Store(); var session = await Resolve(store);
        var request = Request("question");
        request["tools"] = new List<object?> { new D { ["type"] = "custom", ["name"] = "exec" } };
        request["reasoning"] = new D { ["effort"] = "high" };
        request["instructions"] = "old instructions";
        var first = await store.ProjectRequestAsync(session, request, "turn", default);
        await store.CommitResponseAsync(session, first, Response("r1", "answer"), default);
        var next = await store.ProjectRequestAsync(session, new() { ["previous_response_id"] = "r1", ["input"] = new List<object?> { Message("followup") } }, "next-turn", default);
        Assert.Equal(3, JsonDictionaryValue.List(next.Payload, "input").Count);
        Assert.Single(JsonDictionaryValue.List(next.Payload, "tools"));
        Assert.Equal("high", JsonDictionaryValue.String((D)next.Payload["reasoning"]!, "effort"));
        Assert.False(next.Payload.ContainsKey("instructions"));
        Assert.False(next.Payload.ContainsKey("previous_response_id"));
        var replaced = await store.ProjectRequestAsync(session, new() { ["previous_response_id"] = "r1", ["input"] = new List<object?>(), ["tools"] = new List<object?>(), ["reasoning"] = null }, "next-turn", default);
        Assert.Empty(JsonDictionaryValue.List(replaced.Payload, "tools"));
        Assert.Null(replaced.Payload["reasoning"]);
    }

    [Fact]
    public async Task ExplicitTurnAndCanonicalRequestReplayWithoutSwallowingAnotherTurn()
    {
        var store = Store(); var session = await Resolve(store);
        var original = new D { ["model"] = "test", ["input"] = "question", ["reasoning"] = new D { ["effort"] = "high", ["summary"] = "auto" } };
        var reordered = new D { ["reasoning"] = new D { ["summary"] = "auto", ["effort"] = "high" }, ["input"] = "question", ["model"] = "test" };
        var first = await store.ProjectRequestAsync(session, original, "same-turn", default);
        await store.CommitResponseAsync(session, first, Response("r1", "answer"), default);
        var replay = await store.ProjectRequestAsync(session, reordered, "same-turn", default);
        Assert.Equal("r1", replay.ReplayResponse!["id"]);
        Assert.Null((await store.ProjectRequestAsync(session, reordered, "another-turn", default)).ReplayResponse);
        Assert.Null((await store.ProjectRequestAsync(session, reordered, "", default)).ReplayResponse);
        Assert.Null((await store.ProjectRequestAsync(session, Request("different"), "same-turn", default)).ReplayResponse);
    }

    [Fact]
    public async Task ContinuationsAndReplayRestoreAfterRestartButCannotCrossOwnerOrThread()
    {
        var store = Store(); var session = await Resolve(store, persist: true);
        var projection = await store.ProjectRequestAsync(session, Request("question"), "turn", default);
        await store.CommitResponseAsync(session, projection, Response("r1", "answer"), default);
        var restored = Store(); var same = await Resolve(restored, persist: true);
        Assert.NotNull((await restored.ProjectRequestAsync(same, Request("question"), "turn", default)).ReplayResponse);
        var delta = new D { ["previous_response_id"] = "r1", ["input"] = "next" };
        Assert.Equal(3, JsonDictionaryValue.List((await restored.ProjectRequestAsync(same, delta, "next", default)).Payload, "input").Count);
        var otherThread = await Resolve(restored, "other");
        await Assert.ThrowsAsync<BadRequestException>(() => restored.ProjectRequestAsync(otherThread, delta, "next", default));
        var otherOwner = await restored.ResolveAsync(Guid.NewGuid(), Identity("root"), true, 1, default);
        await Assert.ThrowsAsync<BadRequestException>(() => restored.ProjectRequestAsync(otherOwner, delta, "next", default));
    }

    [Fact]
    public async Task MemoryOnlyResponsesWorkUntilRestartAndFullHistoryCanRebuild()
    {
        var store = Store(); var session = await Resolve(store);
        var projection = await store.ProjectRequestAsync(session, Request("question"), "turn", default);
        await store.CommitResponseAsync(session, projection, Response("r1", "answer"), default);
        Assert.NotNull((await store.ProjectRequestAsync(session, Request("question"), "turn", default)).ReplayResponse);
        Assert.False(Directory.Exists(_directory));
        var restarted = Store(); var rebuilt = await Resolve(restarted);
        await Assert.ThrowsAsync<BadRequestException>(() => restarted.ProjectRequestAsync(rebuilt, new() { ["previous_response_id"] = "r1" }, "next", default));
        Assert.Equal("question", ((D)((List<object?>)(await restarted.ProjectRequestAsync(rebuilt, Request("question"), "turn", default)).Payload["input"]!)[0]!)["content"]);
    }

    [Fact]
    public async Task HeaderIdentityChangesAreRejectedAndChildrenCanRebuildWithoutServerReservations()
    {
        var store = Store(); await Resolve(store);
        var childIdentity = Identity("child", "root", "root", "/root/audit");
        var child = await store.ResolveAsync(_owner, childIdentity, false, 1, default);
        Assert.Equal("/root/audit", child.AgentName);
        await Assert.ThrowsAsync<BadRequestException>(() => store.ResolveAsync(_owner, Identity("child", "other", "root", "/root/audit"), false, 1, default));
        await Assert.ThrowsAsync<BadRequestException>(() => store.ResolveAsync(_owner, Identity("child", "root", "other", "/root/audit"), false, 1, default));
        await Assert.ThrowsAsync<BadRequestException>(() => store.ResolveAsync(_owner, Identity("child", "root", "root", "/root/changed"), false, 1, default));
        var restarted = Store();
        Assert.Equal("child", (await restarted.ResolveAsync(_owner, childIdentity, false, 1, default)).ThreadId);
        await Assert.ThrowsAsync<BadRequestException>(() => restarted.ResolveAsync(_owner, Identity("bad", "bad", "root", "/root/audit"), false, 1, default));
    }

    [Fact]
    public async Task ChildQuotaAndThreadGateAreReleasedOnCancellationWithoutStoppingFutureRequests()
    {
        var store = Store(); var root = await Resolve(store);
        var first = await store.ResolveAsync(_owner, Identity("child-a", "root", "root", "/root/a"), false, 1, default);
        var second = await store.ResolveAsync(_owner, Identity("child-b", "root", "root", "/root/b"), false, 1, default);
        await using var rootLease = await store.AcquireRequestAsync(root, default);
        var firstLease = await store.AcquireRequestAsync(first, default);
        using var stop = new CancellationTokenSource();
        var waiting = store.AcquireRequestAsync(second, stop.Token);
        Assert.False(waiting.IsCompleted);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        await store.CancelRequestAsync(first, firstLease.RequestId);
        Assert.True(firstLease.Token.IsCancellationRequested);
        Assert.False(rootLease.Token.IsCancellationRequested);
        await firstLease.DisposeAsync();
        await firstLease.DisposeAsync();
        await using var next = await store.AcquireRequestAsync(second, default);
        Assert.False(next.Token.IsCancellationRequested);
        Assert.NotNull(await store.TryGetAsync(_owner, "root", default));
    }

    [Fact]
    public async Task FailedResponseCannotBeCommittedOrReplayed()
    {
        var store = Store(); var session = await Resolve(store);
        var projection = await store.ProjectRequestAsync(session, Request("question"), "turn", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitResponseAsync(session, projection, new() { ["id"] = "r1", ["status"] = "failed", ["output"] = new List<object?>() }, default));
        Assert.Null((await store.ProjectRequestAsync(session, Request("question"), "turn", default)).ReplayResponse);
    }

    [Fact]
    public async Task DurableReplayRecoversWhenCrashPrecedesResponseReferencePublication()
    {
        var store = Store(); var session = await Resolve(store, persist: true);
        var projection = await store.ProjectRequestAsync(session, Request("question"), "turn", default);
        await store.CommitResponseAsync(session, projection, Response("r1", "answer"), default);
        var responseIndex = Directory.GetDirectories(_directory, "responses", SearchOption.AllDirectories).Single();
        Directory.Delete(responseIndex, true);
        var restored = Store(); var same = await Resolve(restored, persist: true);
        Assert.Equal("r1", (await restored.ProjectRequestAsync(same, Request("question"), "turn", default)).ReplayResponse!["id"]);
        var delta = await restored.ProjectRequestAsync(same, new() { ["previous_response_id"] = "r1", ["input"] = "next" }, "next", default);
        Assert.Equal(3, JsonDictionaryValue.List(delta.Payload, "input").Count);
    }

    [Fact]
    public async Task RetriedToolResponseRetainsCallIdentityAndNextResultAppearsExactlyOnce()
    {
        var store = Store(); var session = await Resolve(store);
        var projection = await store.ProjectRequestAsync(session, Request("question"), "turn", default);
        var response = new D { ["id"] = "r1", ["status"] = "completed", ["output"] = new List<object?> { new D { ["type"] = "custom_tool_call", ["call_id"] = "original-call", ["name"] = "exec", ["input"] = "one side effect" } } };
        await store.CommitResponseAsync(session, projection, response, default);
        var retried = await store.ProjectRequestAsync(session, Request("question"), "turn", default);
        Assert.Equal("original-call", ((D)Assert.Single(JsonDictionaryValue.List(retried.ReplayResponse!, "output"))!)["call_id"]);
        var next = await store.ProjectRequestAsync(session, new() { ["previous_response_id"] = "r1", ["input"] = new List<object?> { new D { ["type"] = "custom_tool_call_output", ["call_id"] = "original-call", ["output"] = "success" } } }, "turn", default);
        Assert.Single(JsonDictionaryValue.List(next.Payload, "input").OfType<D>(), i => JsonDictionaryValue.String(i, "type") == "custom_tool_call");
        Assert.Single(JsonDictionaryValue.List(next.Payload, "input").OfType<D>(), i => JsonDictionaryValue.String(i, "type") == "custom_tool_call_output");
    }

    [Fact]
    public async Task CancelRequestAlsoCancelsAChildWaitingForItsModelPermit()
    {
        var store = Store(); await Resolve(store);
        var first = await store.ResolveAsync(_owner, Identity("a", "root", "root", "/root/a"), false, 1, default);
        var second = await store.ResolveAsync(_owner, Identity("b", "root", "root", "/root/b"), false, 1, default);
        var occupied = await store.AcquireRequestAsync(first, default);
        var waiting = store.AcquireRequestAsync(second, default);
        await store.CancelRequestAsync(second);
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(1))); }
        finally { await occupied.DisposeAsync(); }
        await using var resumed = await store.AcquireRequestAsync(second, default);
        Assert.False(resumed.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task MemoryOnlyRecordsShareLargeImmutablePrefixAndToolSnapshots()
    {
        var store = Store(); var session = await Resolve(store);
        for (var index = 0; index < 5; index++)
        {
            var request = new D { ["model"] = "test", ["input"] = new List<object?> { Message(new string('p', 100_000)), Message("step " + index) },
                ["tools"] = new List<object?> { new D { ["type"] = "custom", ["name"] = "exec", ["description"] = new string('t', 100_000) } } };
            var projected = await store.ProjectRequestAsync(session, request, "turn-" + index, default);
            await store.CommitResponseAsync(session, projected, Response("r" + index, "answer"), default);
        }
        var field = typeof(NativeClientSessionStore).GetField("_memoryResponses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var records = ((System.Collections.IDictionary)field.GetValue(store)!).Values.Cast<object>().ToList();
        var payloads = records.Select(r => (D)r.GetType().GetProperty("Payload")!.GetValue(r)!).ToList();
        Assert.Equal(5, payloads.Count);
        var prefix = JsonDictionaryValue.List(payloads[0], "input")[0];
        foreach (var payload in payloads.Skip(1))
        {
            Assert.Same(prefix, JsonDictionaryValue.List(payload, "input")[0]);
            Assert.Same(payloads[0]["tools"], payload["tools"]);
            Assert.NotSame(payloads[0]["input"], payload["input"]);
        }
    }

    [Fact]
    public async Task MutatingReturnedProjectionOrReplayCannotChangeSharedStoredHistory()
    {
        var store = Store(); var session = await Resolve(store);
        var request = Request("original question");
        var projected = await store.ProjectRequestAsync(session, request, "turn", default);
        await store.CommitResponseAsync(session, projected, Response("r1", "original answer"), default);
        ((D)JsonDictionaryValue.List(projected.Payload, "input")[0]!)["content"] = "mutated request";
        var firstReplay = await store.ProjectRequestAsync(session, request, "turn", default);
        ((D)JsonDictionaryValue.List(firstReplay.Payload, "input")[0]!)["content"] = "mutated replay payload";
        var output = (D)JsonDictionaryValue.List(firstReplay.ReplayResponse!, "output")[0]!;
        ((D)JsonDictionaryValue.List(output, "content")[0]!)["text"] = "mutated answer";
        var replay = await store.ProjectRequestAsync(session, request, "turn", default);
        Assert.Contains("original question", WebSearchPayload.JsonDumps(replay.Payload));
        Assert.Contains("original answer", WebSearchPayload.JsonDumps(replay.ReplayResponse));
        Assert.DoesNotContain("mutated", WebSearchPayload.JsonDumps(replay.Payload));
        var continuation = await store.ProjectRequestAsync(session, new() { ["previous_response_id"] = "r1", ["input"] = "followup" }, "turn", default);
        Assert.DoesNotContain("mutated", WebSearchPayload.JsonDumps(continuation.Payload));
    }

    [Fact]
    public async Task PersistedAgentNameCannotBeReboundAfterRestartBeforeOldChildLoads()
    {
        var store = Store();
        await Resolve(store, persist: true);
        await store.ResolveAsync(_owner, Identity("old-child", "root", "root", "/root/audit"), true, 1, default);
        var restarted = Store();
        await Assert.ThrowsAsync<BadRequestException>(() => restarted.ResolveAsync(_owner,
            Identity("new-child", "root", "root", "/root/audit"), true, 1, default));
        Assert.NotNull(await restarted.TryGetAsync(_owner, "old-child", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildFirstGroupRejectsConflictingRootLimit(bool restart)
    {
        var store = Store();
        await store.ResolveAsync(_owner, Identity("first", "root", "root", "/root/first"), restart, 1, default);
        if (restart) store = Store();
        await Assert.ThrowsAsync<BadRequestException>(() => store.ResolveAsync(_owner, Identity("root"), restart, 2, default));
        var root = await store.ResolveAsync(_owner, Identity("root"), restart, 1, default);
        Assert.Equal(1, root.MaxConcurrentSubagents);
    }

    [Fact]
    public async Task ChildFirstGroupRejectsConflictingRootStoragePolicy()
    {
        var store = Store();
        await store.ResolveAsync(_owner, Identity("first", "root", "root", "/root/first"), false, 1, default);
        await Assert.ThrowsAsync<BadRequestException>(() => store.ResolveAsync(_owner, Identity("root"), true, 1, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupLimitIsSharedByChildFirstSessionsAndSurvivesRestart(bool restart)
    {
        var store = Store();
        await store.ResolveAsync(_owner, Identity("first", "root", "root", "/root/first"), restart, 2, default);
        await store.ResolveAsync(_owner, Identity("root"), restart, 2, default);
        if (restart) store = Store();
        var first = await store.ResolveAsync(_owner, Identity("first", "root", "root", "/root/first"), restart, 1, default);
        var second = await store.ResolveAsync(_owner, Identity("second", "root", "root", "/root/second"), restart, 1, default);
        var third = await store.ResolveAsync(_owner, Identity("third", "root", "root", "/root/third"), restart, 4, default);
        Assert.Equal(2, first.MaxConcurrentSubagents);
        Assert.Equal(2, second.MaxConcurrentSubagents);
        Assert.Equal(2, third.MaxConcurrentSubagents);
        await using var one = await store.AcquireRequestAsync(first, default);
        await using var two = await store.AcquireRequestAsync(second, default);
        using var cancel = new CancellationTokenSource();
        var waiting = store.AcquireRequestAsync(third, cancel.Token);
        Assert.False(waiting.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var other = await store.ResolveAsync(Guid.NewGuid(), Identity("first", "root", "root", "/root/first"), restart, 1, default);
        await using var independent = await store.AcquireRequestAsync(other, default);
    }

    private NativeClientSessionStore Store() => new(_directory);
    private Task<NativeClientSession> Resolve(NativeClientSessionStore store, string thread = "root", bool persist = false) => store.ResolveAsync(_owner, Identity(thread), persist, 1, default);
    private static MultiAgentClientIdentity Identity(string thread, string parent = "", string? root = null, string name = "/root") => new() { ThreadId = thread, ParentThreadId = parent, RootThreadId = root ?? thread, AgentName = name };
    private static D Request(string text) => new() { ["model"] = "test", ["input"] = text };
    private static D Message(string text) => new() { ["type"] = "message", ["role"] = "user", ["content"] = text };
    private static D Response(string id, string text) => new() { ["id"] = id, ["status"] = "completed", ["output"] = new List<object?> { new D { ["type"] = "message", ["role"] = "assistant", ["content"] = new List<object?> { new D { ["type"] = "output_text", ["text"] = text } } } } };
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

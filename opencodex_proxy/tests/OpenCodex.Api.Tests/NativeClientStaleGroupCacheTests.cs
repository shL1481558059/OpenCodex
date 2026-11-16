using Microsoft.AspNetCore.Mvc;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class NativeClientStaleGroupCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ocxp-stale-group-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _owner = Guid.NewGuid();
    private readonly RecordingRedisConnectionProvider _redis = new();
    private readonly TestClock _clock = new();

    [Fact(Timeout = 15_000)]
    public async Task FailedGroupInvalidation_TryGetRestoresEveryMemberAndReplayAfterRestart()
    {
        var cache = new NativeClientStateCache(_redis);
        await SeedRootCachedThenAddDescendants(cache, failInvalidation: true);

        using var restarted = Store(cache);
        await AssertGroupRestored(restarted, expectReplay: true);
    }

    [Fact(Timeout = 15_000)]
    public async Task RedisUnavailableDuringMemberAdd_OldGroupKeyDoesNotHideMembersAfterRestart()
    {
        var cache = new NativeClientStateCache(_redis);
        await SeedRootCachedThenAddDescendants(cache, redisUnavailableDuringAdd: true);

        using var restarted = Store(cache);
        await AssertGroupRestored(restarted, expectReplay: true);
    }

    [Fact(Timeout = 15_000)]
    public async Task FailedGroupInvalidation_MemoryTtlEvictionStillRestoresMembers()
    {
        var cache = new NativeClientStateCache(_redis);
        using var store = Store(cache, new() { SessionTtlSeconds = 1, CleanupIntervalSeconds = 3600 });
        await SeedRootCachedThenAddDescendants(cache, failInvalidation: true, existing: store);

        _clock.Advance(5);
        await store.CleanupAsync();
        Assert.Equal(0, (await store.ResourceUsageAsync()).Sessions);

        await AssertGroupRestored(store, expectReplay: true);
    }

    [Fact(Timeout = 15_000)]
    public async Task FailedGroupInvalidation_AgentNameCannotBeReboundAfterRestart()
    {
        var cache = new NativeClientStateCache(_redis);
        await SeedRootCachedThenAddDescendants(cache, failInvalidation: true);

        using var restarted = Store(cache);
        var conflict = await Assert.ThrowsAsync<BadRequestException>(() =>
            restarted.ResolveAsync(_owner, Identity("other-child", "root", "root", "/root/child"), 1, default));
        Assert.Contains("already attached", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await restarted.TryGetAsync(_owner, "child", default));
        Assert.Null(await restarted.TryGetAsync(_owner, "other-child", default));
    }

    [Fact(Timeout = 15_000)]
    public async Task FailedGroupInvalidation_OwnerIsolationAndReplayStayPrivate()
    {
        var cache = new NativeClientStateCache(_redis);
        await SeedRootCachedThenAddDescendants(cache, failInvalidation: true);

        using var restarted = Store(cache);
        var stranger = Guid.NewGuid();
        Assert.Null(await restarted.TryGetAsync(stranger, "root", default));
        Assert.Null(await restarted.TryGetAsync(stranger, "child", default));
        Assert.Null(await restarted.TryGetAsync(stranger, "grand", default));
        // A stranger may create their own identically named threads, but cannot replay this owner's turn.
        var otherRoot = await restarted.ResolveAsync(stranger, Identity("root"), 1, default);
        var otherReplay = await restarted.ProjectRequestAsync(otherRoot, Request("root-question"), "root-turn", default);
        Assert.Null(otherReplay.ReplayResponse);

        var owned = (await restarted.TryGetAsync(_owner, "root", default))!;
        var replay = await restarted.ProjectRequestAsync(owned, Request("root-question"), "root-turn", default);
        Assert.Equal("root-response", replay.ReplayResponse!["id"]);
    }

    [Fact(Timeout = 15_000)]
    public async Task CacheSetFailure_DoesNotLoseDiskMembersOrReplay()
    {
        var cache = new NativeClientStateCache(_redis);
        using (var store = Store(cache))
        {
            var root = await store.ResolveAsync(_owner, Identity("root"), 1, default);
            await CommitTurn(store, root, "root-turn", "root-question", "root-response");
            _redis.FailSets = true;
            _redis.ThrowOnSet = true;
            var child = await store.ResolveAsync(_owner, Identity("child", "root", "root", "/root/child"), 1, default);
            await CommitTurn(store, child, "child-turn", "child-question", "child-response");
        }

        _redis.FailSets = false;
        _redis.ThrowOnSet = false;
        using var restarted = Store(cache);
        Assert.NotNull(await restarted.TryGetAsync(_owner, "root", default));
        var childSession = Assert.IsType<NativeClientSession>(await restarted.TryGetAsync(_owner, "child", default));
        var replay = await restarted.ProjectRequestAsync(childSession, Request("child-question"), "child-turn", default);
        Assert.Equal("child-response", replay.ReplayResponse!["id"]);
    }

    [Fact(Timeout = 15_000)]
    public async Task UnversionedGroupCache_IsIgnoredAndDiskMembersWin()
    {
        var cache = new NativeClientStateCache(_redis);
        await SeedRootCachedThenAddDescendants(cache, failInvalidation: true);
        _redis.StripGenerationOnRead = true;

        using var restarted = Store(cache);
        await AssertGroupRestored(restarted, expectReplay: true);
    }

    [Fact(Timeout = 15_000)]
    public async Task RedisSessionWithoutDiskFile_IsNotTreatedAsExisting()
    {
        var cache = new NativeClientStateCache(_redis);
        using (var store = Store(cache))
            await store.ResolveAsync(_owner, Identity("root"), 1, default);

        foreach (var folder in Directory.GetDirectories(Path.Combine(_directory, "native-client-v1")))
            Directory.Delete(folder, true);

        using var restarted = Store(cache);
        Assert.Null(await restarted.TryGetAsync(_owner, "root", default));
    }

    [Fact(Timeout = 15_000)]
    public async Task CorruptSessionDocument_FailsConservativelyAndDoesNotInventAGroup()
    {
        var cache = new NativeClientStateCache(_redis);
        using (var store = Store(cache))
            await store.ResolveAsync(_owner, Identity("root"), 1, default);

        var sessionFiles = Directory.GetFiles(Path.Combine(_directory, "native-client-v1"), "session.json", SearchOption.AllDirectories);
        Assert.NotEmpty(sessionFiles);
        await File.WriteAllTextAsync(sessionFiles[0], "{not-json");

        using var restarted = Store(cache);
        await Assert.ThrowsAnyAsync<Exception>(() => restarted.TryGetAsync(_owner, "root", default));
        Assert.Null(await SafeTryGet(restarted, _owner, "missing"));
    }

    [Fact(Timeout = 15_000)]
    public async Task KnownParentChildCanStayNativeAfterStaleCacheRestart()
    {
        var cache = new NativeClientStateCache(_redis);
        await SeedRootCachedThenAddDescendants(cache, failInvalidation: true);

        using var restarted = Store(cache);
        Assert.NotNull(await restarted.TryGetAsync(_owner, "child", default));
        using var client = new MultiAgentApiTestContext(
            context => MultiAgentApiTestContext.Stream(context,
                MultiAgentApiTestContext.Events(MultiAgentApiTestContext.Terminal(MultiAgentApiTestContext.Message("child done")))),
            key: _owner,
            nativeStore: restarted);
        client.Http.Request.Headers["thread-id"] = "child";
        client.Http.Request.Headers["session-id"] = "root";
        client.Http.Request.Headers["x-codex-parent-thread-id"] = "root";
        client.Http.Request.Headers["x-codex-turn-metadata"] =
            """{"thread_id":"child","session_id":"root","parent_thread_id":"root","agent_name":"/root/child","subagent_kind":"thread_spawn"}""";

        var request = MultiAgentApiTestContext.Request();
        request["model"] = "explicit-child-model";
        var result = await client.Service.TryClientResponsesAsync(request, allowCreate: false);

        Assert.NotNull(result);
        var body = Assert.IsType<ObjectResult>(result);
        Assert.Equal("completed", Assert.IsType<D>(body.Value)["status"]);
        Assert.Equal("explicit-child-model", Assert.Single(client.FakeEndpoint.Calls).Payload!["model"]);
        Assert.NotNull(await restarted.TryGetAsync(_owner, "child", default));
    }

    [Fact(Timeout = 15_000)]
    public async Task KnownParentGrandchildStaysNativeWhenAllowCreateIsFalse()
    {
        var cache = new NativeClientStateCache(_redis);
        await SeedRootCachedThenAddDescendants(cache, failInvalidation: true);

        using var restarted = Store(cache);
        using var client = new MultiAgentApiTestContext(
            context => MultiAgentApiTestContext.Stream(context,
                MultiAgentApiTestContext.Events(MultiAgentApiTestContext.Terminal(MultiAgentApiTestContext.Message("grand done")))),
            key: _owner,
            nativeStore: restarted);
        client.Http.Request.Headers["thread-id"] = "grand";
        client.Http.Request.Headers["session-id"] = "root";
        client.Http.Request.Headers["x-codex-parent-thread-id"] = "child";
        client.Http.Request.Headers["x-codex-turn-metadata"] =
            """{"thread_id":"grand","session_id":"root","parent_thread_id":"child","agent_name":"/root/child/grand","subagent_kind":"thread_spawn"}""";

        var request = MultiAgentApiTestContext.Request();
        request["model"] = "explicit-grand-model";
        var result = await client.Service.TryClientResponsesAsync(request, allowCreate: false);

        Assert.NotNull(result);
        Assert.Equal("explicit-grand-model", Assert.Single(client.FakeEndpoint.Calls).Payload!["model"]);
        Assert.NotNull(await restarted.TryGetAsync(_owner, "grand", default));
        Assert.NotNull(await restarted.TryGetAsync(_owner, "child", default));
    }

    [Fact(Timeout = 15_000)]
    public async Task DelayedStaleGroupCacheWriteDoesNotHideALaterMember()
    {
        var cache = new NativeClientStateCache(_redis);
        await SeedRootCachedThenAddDescendants(cache, failInvalidation: false);
        await cache.SetGroupAsync(new NativeClientCachedGroup
        {
            Owner = _owner,
            RootThreadId = "root",
            MaxConcurrentSubagents = 1,
            Sessions =
            [
                new NativeClientCachedSession
                {
                    Owner = _owner,
                    ThreadId = "root",
                    RootThreadId = "root",
                    AgentName = "/root",
                    MaxConcurrentSubagents = 1
                }
            ]
        }, default);

        using var restarted = Store(cache);
        Assert.NotNull(await restarted.TryGetAsync(_owner, "child", default));
        Assert.NotNull(await restarted.TryGetAsync(_owner, "grand", default));
        await Assert.ThrowsAsync<BadRequestException>(() =>
            restarted.ResolveAsync(_owner, Identity("rebinder", "root", "root", "/root/child"), 1, default));
    }

    private async Task SeedRootCachedThenAddDescendants(NativeClientStateCache cache,
        bool failInvalidation = false, bool redisUnavailableDuringAdd = false, NativeClientSessionStore? existing = null)
    {
        var store = existing ?? Store(cache);
        var created = existing is null;
        try
        {
            var root = await store.ResolveAsync(_owner, Identity("root"), 1, default);
            await CommitTurn(store, root, "root-turn", "root-question", "root-response");

            // Force a disk-backed restore so Redis holds a root-only group snapshot.
            if (existing is not null)
            {
                _clock.Advance(5);
                await store.CleanupAsync();
            }
            else
            {
                store.Dispose();
                store = Store(cache);
                created = true;
            }
            Assert.NotNull(await store.TryGetAsync(_owner, "root", default));

            if (failInvalidation) _redis.IgnoreDeletes = true;
            if (redisUnavailableDuringAdd) _redis.IsAvailable = false;

            var child = await store.ResolveAsync(_owner, Identity("child", "root", "root", "/root/child"), 1, default);
            await CommitTurn(store, child, "child-turn", "child-question", "child-response");
            var grand = await store.ResolveAsync(_owner, Identity("grand", "child", "root", "/root/child/grand"), 1, default);
            await CommitTurn(store, grand, "grand-turn", "grand-question", "grand-response");
        }
        finally
        {
            _redis.IgnoreDeletes = false;
            _redis.IsAvailable = true;
            if (created) store.Dispose();
        }
    }

    private async Task AssertGroupRestored(NativeClientSessionStore store, bool expectReplay)
    {
        var root = Assert.IsType<NativeClientSession>(await store.TryGetAsync(_owner, "root", default));
        var child = Assert.IsType<NativeClientSession>(await store.TryGetAsync(_owner, "child", default));
        var grand = Assert.IsType<NativeClientSession>(await store.TryGetAsync(_owner, "grand", default));
        Assert.Equal(_owner, root.Owner);
        Assert.Equal(_owner, child.Owner);
        Assert.Equal(_owner, grand.Owner);
        Assert.Equal("root", child.ParentThreadId);
        Assert.Equal("child", grand.ParentThreadId);
        Assert.Equal("/root", root.AgentName);
        Assert.Equal("/root/child", child.AgentName);
        Assert.Equal("/root/child/grand", grand.AgentName);
        Assert.Equal("root", root.RootThreadId);
        Assert.Equal("root", child.RootThreadId);
        Assert.Equal("root", grand.RootThreadId);
        if (!expectReplay) return;
        Assert.Equal("root-response", (await store.ProjectRequestAsync(root, Request("root-question"), "root-turn", default)).ReplayResponse!["id"]);
        Assert.Equal("child-response", (await store.ProjectRequestAsync(child, Request("child-question"), "child-turn", default)).ReplayResponse!["id"]);
        Assert.Equal("grand-response", (await store.ProjectRequestAsync(grand, Request("grand-question"), "grand-turn", default)).ReplayResponse!["id"]);
    }

    private NativeClientSessionStore Store(NativeClientStateCache cache, NativeClientCacheOptions? options = null)
        => new(_directory, options ?? new() { SessionTtlSeconds = 1, CleanupIntervalSeconds = 3600 }, _clock, cache);

    private static MultiAgentClientIdentity Identity(string thread, string parent = "", string? root = null, string name = "/root")
        => new() { ThreadId = thread, ParentThreadId = parent, RootThreadId = root ?? thread, AgentName = name };

    private static D Request(string text) => new() { ["model"] = "test", ["input"] = text };

    private static D Response(string id, string text) => new()
    {
        ["id"] = id,
        ["status"] = "completed",
        ["output"] = new List<object?>
        {
            new D
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new List<object?> { new D { ["type"] = "output_text", ["text"] = text } }
            }
        }
    };

    private static async Task CommitTurn(NativeClientSessionStore store, NativeClientSession session,
        string turn, string question, string responseId)
    {
        var projected = await store.ProjectRequestAsync(session, Request(question), turn, default);
        await store.CommitResponseAsync(session, projected, Response(responseId, question + " answer"), default);
    }

    private static async Task<NativeClientSession?> SafeTryGet(NativeClientSessionStore store, Guid owner, string thread)
    {
        try { return await store.TryGetAsync(owner, thread, default); }
        catch { return null; }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

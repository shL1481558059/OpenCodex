using System.Reflection;
using OpenCodex.Core.Services.Caching;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using StackExchange.Redis;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class NativeClientStateCacheTests
{
    [Fact]
    public async Task GroupCacheWritesTwentyFourHoursAndRefreshesOnHit()
    {
        var redis = new RecordingRedisConnectionProvider();
        var cache = new NativeClientStateCache(redis);
        var owner = Guid.NewGuid();
        var group = new NativeClientCachedGroup
        {
            Owner = owner,
            RootThreadId = "root",
            Generation = 1,
            MaxConcurrentSubagents = 2,
            Sessions =
            [
                new NativeClientCachedSession
                {
                    Owner = owner,
                    ThreadId = "root",
                    RootThreadId = "root",
                    AgentName = "/root",
                    MaxConcurrentSubagents = 2
                }
            ]
        };

        await cache.SetGroupAsync(group, default);
        Assert.Equal((Expiration)TimeSpan.FromHours(24), redis.LastSetExpiry);
        var restored = await cache.GetGroupAsync(owner, "root", default);
        Assert.Equal("root", restored!.RootThreadId);
        Assert.Null(redis.LastRefreshExpiry);
        await cache.TouchGroupAsync(owner, "root", default);
        Assert.Equal(TimeSpan.FromHours(24), redis.LastRefreshExpiry);
    }

    [Fact]
    public async Task ResponsePayloadsAreNotWrittenToRedis()
    {
        var redis = new RecordingRedisConnectionProvider();
        var cache = new NativeClientStateCache(redis);
        var owner = Guid.NewGuid();
        var payload = new D { ["model"] = "test", ["input"] = "question" };
        var response = new D { ["id"] = "r1", ["status"] = "completed", ["output"] = new List<object?>() };
        using var store = new NativeClientSessionStore(
            Path.Combine(Path.GetTempPath(), "native-cache-response-" + Guid.NewGuid().ToString("N")),
            cache: cache);
        var session = await store.ResolveAsync(owner, new MultiAgentClientIdentity
        {
            ThreadId = "root",
            RootThreadId = "root",
            AgentName = "/root"
        }, 1, default);
        var projection = await store.ProjectRequestAsync(session, payload, "turn-key", default);
        var setsBefore = redis.SetCount;

        await store.CommitResponseAsync(session, projection, response, default);

        Assert.Equal(setsBefore, redis.SetCount);
        Assert.Equal("r1", (await store.ProjectRequestAsync(session, payload, "turn-key", default)).ReplayResponse!["id"]);
    }

    [Fact]
    public async Task OversizeValueIsNotWrittenToRedis()
    {
        var redis = new RecordingRedisConnectionProvider();
        var cache = new NativeClientStateCache(redis, new() { RedisMaxValueBytes = 64 });
        var owner = Guid.NewGuid();
        await cache.SetGroupAsync(new()
        {
            Owner = owner,
            RootThreadId = new string('r', 500),
            MaxConcurrentSubagents = 1
        }, default);
        Assert.Equal(0, redis.SetCount);
    }

    [Fact]
    public async Task StoreRestoresSessionAndReplayFromDiskAfterRedisLoss()
    {
        var directory = Path.Combine(Path.GetTempPath(), "native-cache-fallback-" + Guid.NewGuid().ToString("N"));
        var redis = new RecordingRedisConnectionProvider();
        var cache = new NativeClientStateCache(redis);
        var owner = Guid.NewGuid();
        var identity = new MultiAgentClientIdentity
        {
            ThreadId = "root",
            RootThreadId = "root",
            AgentName = "/root"
        };
        try
        {
            using (var store = new NativeClientSessionStore(directory, cache: cache))
            {
                var session = await store.ResolveAsync(owner, identity, 1, default);
                var request = new D { ["model"] = "test", ["input"] = "question" };
                var projection = await store.ProjectRequestAsync(session, request, "turn", default);
                await store.CommitResponseAsync(session, projection,
                    new D { ["id"] = "r1", ["status"] = "completed", ["output"] = new List<object?>() }, default);
            }

            redis.Clear();
            using var restarted = new NativeClientSessionStore(directory, cache: cache);
            var restored = Assert.IsType<NativeClientSession>(await restarted.TryGetAsync(owner, "root", default));
            var replay = await restarted.ProjectRequestAsync(restored,
                new D { ["model"] = "test", ["input"] = "question" }, "turn", default);
            Assert.Equal("r1", replay.ReplayResponse!["id"]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

}

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
    public async Task ResponseCacheRoundTripsExactPayloadAndResponseJson()
    {
        var redis = new RecordingRedisConnectionProvider();
        var cache = new NativeClientStateCache(redis);
        var owner = Guid.NewGuid();
        var payload = new D { ["model"] = "test", ["input"] = "question" };
        var response = new D { ["id"] = "r1", ["status"] = "completed", ["output"] = new List<object?>() };
        var record = new NativeClientCachedResponse
        {
            Owner = owner,
            ThreadId = "root",
            ResponseId = "r1",
            ReplayKey = "turn-key",
            PayloadJson = WebSearchPayload.JsonDumps(payload),
            ResponseJson = WebSearchPayload.JsonDumps(response)
        };

        await cache.SetResponseAsync(record, default);
        await cache.SetReplayAsync(record, default);
        var byResponse = await cache.GetResponseAsync(owner, "root", "r1", default);
        var byReplay = await cache.GetReplayAsync(owner, "root", "turn-key", default);
        Assert.Equal("test", byResponse!.Payload()["model"]);
        Assert.Equal("r1", byReplay!.Response()["id"]);
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

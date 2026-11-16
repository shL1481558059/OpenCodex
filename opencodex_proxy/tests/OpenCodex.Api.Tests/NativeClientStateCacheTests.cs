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

    private sealed class RecordingRedisConnectionProvider : IRedisConnectionProvider
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public RecordingRedisConnectionProvider()
        {
            var database = DispatchProxy.Create<IDatabase, RedisDatabaseProxy>();
            ((RedisDatabaseProxy)(object)database).Provider = this;
            Database = database;
        }

        public IDatabase Database { get; }
        public bool IsAvailable => true;
        public string KeyPrefix => "test";
        public int SetCount { get; private set; }
        public Expiration? LastSetExpiry { get; private set; }
        public TimeSpan? LastRefreshExpiry { get; private set; }
        public IDatabase? GetDatabase(int db = -1) => Database;
        public ISubscriber? GetSubscriber() => null;

        internal Task<bool> SetAsync(string key, string value, Expiration expiry)
        {
            _values[key] = value;
            SetCount++;
            LastSetExpiry = expiry;
            return Task.FromResult(true);
        }

        internal Task<RedisValue> GetAsync(string key)
            => Task.FromResult(_values.TryGetValue(key, out var value) ? (RedisValue)value : RedisValue.Null);

        internal Task<bool> ExpireAsync(string key, TimeSpan? expiry)
        {
            LastRefreshExpiry = expiry;
            return Task.FromResult(_values.ContainsKey(key));
        }

        internal Task<bool> DeleteAsync(string key) => Task.FromResult(_values.Remove(key));

        internal void Clear() => _values.Clear();
    }

    private class RedisDatabaseProxy : DispatchProxy
    {
        internal RecordingRedisConnectionProvider Provider { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDatabase.StringSetAsync) && args is { Length: >= 3 })
                return Provider.SetAsync(args[0]!.ToString()!, args[1]!.ToString()!, (Expiration)args[2]!);
            if (targetMethod?.Name == nameof(IDatabase.StringGetAsync) && args is { Length: >= 1 })
                return Provider.GetAsync(args[0]!.ToString()!);
            if (targetMethod?.Name == nameof(IDatabase.KeyExpireAsync) && args is { Length: >= 2 })
                return Provider.ExpireAsync(args[0]!.ToString()!, (TimeSpan?)args[1]);
            if (targetMethod?.Name == nameof(IDatabase.KeyDeleteAsync) && args is { Length: >= 1 })
                return Provider.DeleteAsync(args[0]!.ToString()!);
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}

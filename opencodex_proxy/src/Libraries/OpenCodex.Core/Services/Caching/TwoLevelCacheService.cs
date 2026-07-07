using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using OpenCodex.CoreBase.Caching;
using StackExchange.Redis;

namespace OpenCodex.Core.Services.Caching;

/// <summary>
/// 两级缓存实现:L1 进程内 <see cref="IMemoryCache"/> + L2 Redis。
/// </summary>
/// <remarks>
/// 读路径:L1 命中直接返回 → L2 命中回写 L1 → 都未命中执行 factory 回源并逐层写回。
/// 写路径(失效):删本地 L1 → 删 Redis L2 → 通过 Pub/Sub 广播,通知其它实例删各自 L1。
/// Redis 不可用时自动降级为纯 L1,任何 Redis 异常都被吞掉、不影响主流程。
/// 不缓存 null(负结果),避免为已失效值维护额外失效逻辑。
/// </remarks>
public sealed class TwoLevelCacheService : ICacheService, IDisposable
{
    private const string InvalidationChannelSuffix = "cache-invalidation";

    private readonly IMemoryCache _l1;
    private readonly IRedisConnectionProvider _redis;
    private readonly TimeSpan _defaultTtl;

    /// <summary>
    /// 本实例唯一标识,用于在失效广播中跳过自己发出的消息(自己已在本地删过)。
    /// </summary>
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    private readonly RedisChannel _invalidationChannel;
    private bool _subscribed;
    private readonly object _subscribeLock = new();

    public TwoLevelCacheService(
        IMemoryCache memoryCache,
        IRedisConnectionProvider redis,
        TimeSpan defaultTtl)
    {
        _l1 = memoryCache;
        _redis = redis;
        _defaultTtl = defaultTtl > TimeSpan.Zero ? defaultTtl : TimeSpan.FromSeconds(300);

        // 订阅频道名不经过 WithKeyPrefix(那只作用于 key),这里手动挂前缀保持隔离。
        var channelName = string.IsNullOrWhiteSpace(_redis.KeyPrefix)
            ? InvalidationChannelSuffix
            : $"{_redis.KeyPrefix}:{InvalidationChannelSuffix}";
        _invalidationChannel = RedisChannel.Literal(channelName);

        TryEnsureSubscribed();
    }

    /// <inheritdoc />
    public async Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T?>> factory, TimeSpan? ttl = null)
    {
        // 1. L1
        if (_l1.TryGetValue(key, out var cached) && cached is T l1Hit)
        {
            return l1Hit;
        }

        // Redis 若在启动后才连上,这里让订阅自愈(已订阅时为廉价空操作)。
        TryEnsureSubscribed();

        var effectiveTtl = ttl.HasValue && ttl.Value > TimeSpan.Zero ? ttl.Value : _defaultTtl;

        // 2. L2(Redis)
        var db = SafeGetDatabase();
        if (db is not null)
        {
            try
            {
                var raw = await db.StringGetAsync(key).ConfigureAwait(false);
                if (raw.HasValue)
                {
                    var fromL2 = Deserialize<T>(raw!);
                    if (fromL2 is not null)
                    {
                        SetL1(key, fromL2, effectiveTtl);
                        return fromL2;
                    }
                }
            }
            catch
            {
                // Redis 读失败:降级继续走回源。
            }
        }

        // 3. 回源
        var value = await factory().ConfigureAwait(false);
        if (value is null)
        {
            return default;
        }

        // 4. 逐层写回
        SetL1(key, value, effectiveTtl);
        if (db is not null)
        {
            try
            {
                await db.StringSetAsync(key, Serialize(value), effectiveTtl).ConfigureAwait(false);
            }
            catch
            {
                // Redis 写失败:仅保留 L1,不影响返回。
            }
        }

        return value;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key)
    {
        return RemoveAsync(new[] { key });
    }

    /// <inheritdoc />
    public async Task RemoveAsync(IEnumerable<string> keys)
    {
        var list = keys?.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
        if (list is null || list.Count == 0)
        {
            return;
        }

        // Redis 若在启动后才连上,这里让订阅自愈(已订阅时为廉价空操作)。
        TryEnsureSubscribed();

        // 1. 本地 L1
        foreach (var key in list)
        {
            _l1.Remove(key);
        }

        var db = SafeGetDatabase();
        var subscriber = SafeGetSubscriber();
        foreach (var key in list)
        {
            // 2. Redis L2
            if (db is not null)
            {
                try
                {
                    await db.KeyDeleteAsync(key).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略:失效尽力而为,L1 已删 + TTL 兜底。
                }
            }

            // 3. 广播失效,通知其它实例删各自 L1。
            if (subscriber is not null)
            {
                try
                {
                    await subscriber.PublishAsync(_invalidationChannel, $"{_instanceId}|{key}").ConfigureAwait(false);
                }
                catch
                {
                    // 忽略广播失败。
                }
            }
        }
    }

    private void SetL1<T>(string key, T value, TimeSpan ttl)
    {
        _l1.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        });
    }

    private IDatabase? SafeGetDatabase()
    {
        try
        {
            return _redis.IsAvailable ? _redis.GetDatabase() : null;
        }
        catch
        {
            return null;
        }
    }

    private ISubscriber? SafeGetSubscriber()
    {
        try
        {
            return _redis.IsAvailable ? _redis.GetSubscriber() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 首次可用时订阅失效频道;订阅只需成功一次。
    /// </summary>
    private void TryEnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        lock (_subscribeLock)
        {
            if (_subscribed)
            {
                return;
            }

            var subscriber = SafeGetSubscriber();
            if (subscriber is null)
            {
                return;
            }

            try
            {
                subscriber.Subscribe(_invalidationChannel, OnInvalidationMessage);
                _subscribed = true;
            }
            catch
            {
                // 订阅失败:保持未订阅,后续仍可纯 L1 + TTL 运行。
            }
        }
    }

    private void OnInvalidationMessage(RedisChannel channel, RedisValue message)
    {
        if (!message.HasValue)
        {
            return;
        }

        var text = message.ToString();
        var separatorIndex = text.IndexOf('|');
        if (separatorIndex <= 0 || separatorIndex >= text.Length - 1)
        {
            return;
        }

        var senderId = text[..separatorIndex];
        if (string.Equals(senderId, _instanceId, StringComparison.Ordinal))
        {
            // 自己发出的失效,本地已删,跳过。
            return;
        }

        var key = text[(separatorIndex + 1)..];
        _l1.Remove(key);
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static T? Deserialize<T>(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw);
        }
        catch
        {
            return default;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_subscribed)
            {
                SafeGetSubscriber()?.Unsubscribe(_invalidationChannel);
            }
        }
        catch
        {
            // 忽略清理异常。
        }
    }
}

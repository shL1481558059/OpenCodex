using System.Collections.Concurrent;
using OpenCodex.Core.Config;
using OpenCodex.Core.Services.Caching;
using OpenCodex.CoreBase.Services.Proxy;
using StackExchange.Redis;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 渠道并发容量限制服务。Redis 可用时跨实例共享限流(分布式信号量),不可用时降级为进程内计数。
/// </summary>
/// <remarks>
/// Redis 路径:每个 (owner,channel) 一个 Sorted Set,member=leaseId(GUID),score=租约过期时间戳。
/// TryAcquire 用分布式锁(LockTake/LockRelease)保护"清理过期 + 判满 + 占位"三步操作。
/// Release 用 fire-and-forget ZREM。
/// 实例崩溃未释放的槽位靠租约 TTL(默认 600s)自动回收,避免容量永久卡死。
/// 进程内 CounterEntry 始终维护(无论是否走 Redis),供 GetActiveRequests/GetActiveModelUsages 读取——
/// 多实例下这两个值为本实例视角的近似(仅用于最小连接排序启发式与管理台展示),全局硬限流以 Redis 为准。
/// </remarks>
public sealed class ChannelCapacityService : IChannelCapacityService
{
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(600);
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(5);
    private const int LockRetryCount = 3;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(10);

    private readonly IRedisConnectionProvider? _redis;
    private readonly ConcurrentDictionary<string, CounterEntry> _entries = new(StringComparer.Ordinal);

    public ChannelCapacityService(IRedisConnectionProvider? redis = null)
    {
        _redis = redis;
    }

    /// <inheritdoc />
    public async Task<IChannelCapacityLease?> TryAcquireAsync(
        string ownerUsername,
        IReadOnlyDictionary<string, object?> channel,
        string? requestModel = null,
        string? upstreamModel = null)
    {
        var channelId = ChannelId(channel);
        var modelUsageKey = new ModelUsageKey(CleanModel(requestModel), CleanModel(upstreamModel));
        var tracksModelUsage = modelUsageKey.Model is not null || modelUsageKey.UpstreamModel is not null;
        var capacity = channel.TryGetValue("capacity", out var capacityValue)
                       && capacityValue is int capacityInt
                       && capacityInt > 0
            ? capacityInt
            : 0;

        var db = GetDatabase();

        // 容量 > 0 且 Redis 可用:分布式锁保护"清理过期 + 判满 + 占位";失败则直接拒绝
        string? leaseId = null;
        if (capacity > 0 && db is not null)
        {
            leaseId = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expiry = now + (long)LeaseTtl.TotalSeconds;
            var acquired = await TryAcquireRedisSlotAsync(
                db,
                CapacityKey(ownerUsername, channelId),
                CapacityLockKey(ownerUsername, channelId),
                now,
                expiry,
                capacity,
                leaseId);
            if (!acquired)
            {
                return null;
            }
        }

        // 进程内计数始终维护(供 GetActiveRequests/GetActiveModelUsages);Redis 不可用时也承担限流
        var entry = _entries.GetOrAdd(Key(ownerUsername, channelId), static _ => new CounterEntry());
        lock (entry.Sync)
        {
            if (leaseId is null && capacity > 0 && entry.ActiveRequests >= capacity)
            {
                // 仅 Redis 不可用的降级路径在此判满;Redis 路径已由 Lua 判满
                return null;
            }

            entry.ActiveRequests++;
            if (tracksModelUsage)
            {
                entry.ActiveModelRequests.TryGetValue(modelUsageKey, out var count);
                entry.ActiveModelRequests[modelUsageKey] = count + 1;
            }
        }

        return new Lease(this, ownerUsername, channelId, modelUsageKey, tracksModelUsage, leaseId, db);
    }

    /// <inheritdoc />
    public int GetActiveRequests(string ownerUsername, string channelId)
    {
        if (!_entries.TryGetValue(Key(ownerUsername, channelId), out var entry))
        {
            return 0;
        }

        lock (entry.Sync)
        {
            return entry.ActiveRequests;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ChannelActiveModelUsage> GetActiveModelUsages(string ownerUsername, string channelId)
    {
        if (!_entries.TryGetValue(Key(ownerUsername, channelId), out var entry))
        {
            return [];
        }

        lock (entry.Sync)
        {
            return entry.ActiveModelRequests
                .Where(item => item.Value > 0)
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key.Model ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Key.UpstreamModel ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(item => new ChannelActiveModelUsage(
                    item.Key.Model,
                    item.Key.UpstreamModel,
                    item.Value))
                .ToList();
        }
    }

    private void Release(
        string ownerUsername,
        string channelId,
        ModelUsageKey modelUsageKey,
        bool tracksModelUsage,
        string? leaseId,
        IDatabase? db)
    {
        // Redis 路径:fire-and-forget 释放全局槽位;丢失则靠租约 TTL 自动回收
        if (leaseId is not null && db is not null)
        {
            db.SortedSetRemove(
                CapacityKey(ownerUsername, channelId),
                leaseId,
                CommandFlags.FireAndForget);
        }

        var key = Key(ownerUsername, channelId);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return;
        }

        var shouldRemove = false;
        lock (entry.Sync)
        {
            if (entry.ActiveRequests > 0)
            {
                entry.ActiveRequests--;
            }

            if (tracksModelUsage
                && entry.ActiveModelRequests.TryGetValue(modelUsageKey, out var modelUsageCount))
            {
                if (modelUsageCount <= 1)
                {
                    entry.ActiveModelRequests.Remove(modelUsageKey);
                }
                else
                {
                    entry.ActiveModelRequests[modelUsageKey] = modelUsageCount - 1;
                }
            }

            shouldRemove = entry.ActiveRequests == 0 && entry.ActiveModelRequests.Count == 0;
        }

        if (shouldRemove)
        {
            _entries.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// 分布式锁保护下的 Sorted Set 信号量获取:清理过期 → 判满 → 占位。
    /// </summary>
    private async Task<bool> TryAcquireRedisSlotAsync(
        IDatabase db,
        string setKey,
        string lockKey,
        long now,
        long expiry,
        int capacity,
        string leaseId)
    {
        var lockToken = leaseId;
        var redisKey = (RedisKey)setKey;
        var redisLockKey = (RedisKey)lockKey;

        for (var attempt = 0; attempt < LockRetryCount; attempt++)
        {
            if (await db.LockTakeAsync(redisLockKey, lockToken, LockTtl).ConfigureAwait(false))
            {
                try
                {
                    // 清理过期租约
                    await db.SortedSetRemoveRangeByScoreAsync(redisKey, double.NegativeInfinity, now)
                        .ConfigureAwait(false);
                    // 判满
                    var current = await db.SortedSetLengthAsync(redisKey).ConfigureAwait(false);
                    if (current >= capacity)
                    {
                        return false;
                    }
                    // 占位
                    await db.SortedSetAddAsync(redisKey, leaseId, expiry).ConfigureAwait(false);
                    return true;
                }
                finally
                {
                    db.LockRelease(redisLockKey, lockToken, CommandFlags.FireAndForget);
                }
            }

            if (attempt < LockRetryCount - 1)
            {
                await Task.Delay(LockRetryDelay).ConfigureAwait(false);
            }
        }

        // 锁获取失败:降级为直接尝试占位(无锁,极端竞态下可能轻微超限,靠租约 TTL 兜底)
        await db.SortedSetRemoveRangeByScoreAsync(redisKey, double.NegativeInfinity, now)
            .ConfigureAwait(false);
        var count = await db.SortedSetLengthAsync(redisKey).ConfigureAwait(false);
        if (count >= capacity)
        {
            return false;
        }
        await db.SortedSetAddAsync(redisKey, leaseId, expiry).ConfigureAwait(false);
        return true;
    }

    private IDatabase? GetDatabase()
    {
        return _redis is not null && _redis.IsAvailable ? _redis.GetDatabase() : null;
    }

    private static string CapacityKey(string ownerUsername, string channelId)
    {
        return $"capacity:{ownerUsername.Trim()}:{channelId}";
    }

    private static string CapacityLockKey(string ownerUsername, string channelId)
    {
        return $"capacity:lock:{ownerUsername.Trim()}:{channelId}";
    }

    private static string ChannelId(IReadOnlyDictionary<string, object?> channel)
    {
        return channel.TryGetValue("id", out var value)
            ? ConfigValue.PythonString(value).Trim()
            : string.Empty;
    }

    private static string? CleanModel(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string Key(string ownerUsername, string channelId)
    {
        return $"{ownerUsername.Trim()}\n{channelId}";
    }

    private readonly record struct ModelUsageKey(string? Model, string? UpstreamModel);

    private sealed class CounterEntry
    {
        public object Sync { get; } = new();

        public int ActiveRequests { get; set; }

        public Dictionary<ModelUsageKey, int> ActiveModelRequests { get; } = new();
    }

    private sealed class Lease : IChannelCapacityLease
    {
        private readonly ChannelCapacityService _owner;
        private readonly string _ownerUsername;
        private readonly string _channelId;
        private readonly ModelUsageKey _modelUsageKey;
        private readonly bool _tracksModelUsage;
        private readonly string? _leaseId; // 非 null 表示 Redis 路径
        private readonly IDatabase? _db;
        private int _disposed;

        public Lease(
            ChannelCapacityService owner,
            string ownerUsername,
            string channelId,
            ModelUsageKey modelUsageKey,
            bool tracksModelUsage,
            string? leaseId,
            IDatabase? db)
        {
            _owner = owner;
            _ownerUsername = ownerUsername;
            _channelId = channelId;
            _modelUsageKey = modelUsageKey;
            _tracksModelUsage = tracksModelUsage;
            _leaseId = leaseId;
            _db = db;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Release(_ownerUsername, _channelId, _modelUsageKey, _tracksModelUsage, _leaseId, _db);
        }
    }
}

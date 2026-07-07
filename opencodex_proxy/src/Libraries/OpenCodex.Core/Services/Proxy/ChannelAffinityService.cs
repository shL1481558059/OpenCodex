using System.Collections.Concurrent;
using OpenCodex.Core.Services.Caching;
using OpenCodex.CoreBase.Services.Proxy;
using StackExchange.Redis;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 会话-渠道亲和映射服务，带滑动过期。Redis 可用时跨实例共享，不可用时降级为进程内内存。
/// </summary>
/// <remarks>
/// Redis 路径:key 为 affinity:{owner}:{stickyKey},值 channelId。读时 GET+EXPIRE 刷新滑动过期,
/// 写时 SETEX。进程内降级保留原 ConcurrentDictionary 实现,单实例零延迟。
/// </remarks>
public sealed class ChannelAffinityService : IChannelAffinityService
{
    /// <summary>
    /// 映射的默认存活时长(滑动过期)。
    /// </summary>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(30);

    private readonly IRedisConnectionProvider? _redis;
    private readonly TimeSpan _timeToLive;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// 使用默认存活时长与系统时钟初始化。Redis provider 可选:为 null 或不可用时降级为进程内内存。
    /// </summary>
    public ChannelAffinityService(IRedisConnectionProvider? redis = null)
        : this(DefaultTimeToLive, () => DateTimeOffset.UtcNow, redis)
    {
    }

    /// <summary>
    /// 使用指定存活时长与时钟初始化,主要用于测试注入。
    /// </summary>
    /// <param name="timeToLive">映射的滑动过期时长,必须为正值。</param>
    /// <param name="clock">当前时间提供器。</param>
    /// <param name="redis">Redis 连接提供器,可选。</param>
    public ChannelAffinityService(TimeSpan timeToLive, Func<DateTimeOffset> clock, IRedisConnectionProvider? redis = null)
    {
        _timeToLive = timeToLive > TimeSpan.Zero ? timeToLive : DefaultTimeToLive;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _redis = redis;
    }

    /// <inheritdoc />
    public async Task<string?> GetPreferredChannelIdAsync(string ownerUsername, string stickyKey)
    {
        if (string.IsNullOrEmpty(stickyKey))
        {
            return null;
        }

        var db = GetDatabase();
        if (db is not null)
        {
            var key = AffinityKey(ownerUsername, stickyKey);
            var value = await db.StringGetAsync(key).ConfigureAwait(false);
            if (!value.HasValue)
            {
                return null;
            }

            // 滑动过期:命中时刷新 TTL。与 GET 非原子,但竞态无害(key 过期则下次返回 null)。
            await db.KeyExpireAsync(key, _timeToLive).ConfigureAwait(false);
            return value.ToString();
        }

        return GetPreferredInMemory(ownerUsername, stickyKey);
    }

    /// <inheritdoc />
    public async Task RememberAsync(string ownerUsername, string stickyKey, string channelId)
    {
        if (string.IsNullOrEmpty(stickyKey) || string.IsNullOrEmpty(channelId))
        {
            return;
        }

        var db = GetDatabase();
        if (db is not null)
        {
            var key = AffinityKey(ownerUsername, stickyKey);
            await db.StringSetAsync(key, channelId, _timeToLive).ConfigureAwait(false);
            return;
        }

        RememberInMemory(ownerUsername, stickyKey, channelId);
    }

    private IDatabase? GetDatabase()
    {
        return _redis is not null && _redis.IsAvailable ? _redis.GetDatabase() : null;
    }

    private static string AffinityKey(string ownerUsername, string stickyKey)
    {
        return $"affinity:{ownerUsername.Trim()}:{stickyKey}";
    }

    private string? GetPreferredInMemory(string ownerUsername, string stickyKey)
    {
        var key = InMemoryKey(ownerUsername, stickyKey);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        var now = _clock();
        lock (entry.Sync)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
                return null;
            }

            // 滑动过期:读取也算一次活跃访问,延长有效期。
            entry.ExpiresAt = now + _timeToLive;
            return entry.ChannelId;
        }
    }

    private void RememberInMemory(string ownerUsername, string stickyKey, string channelId)
    {
        var now = _clock();
        var key = InMemoryKey(ownerUsername, stickyKey);
        var entry = _entries.GetOrAdd(key, static _ => new Entry());
        lock (entry.Sync)
        {
            entry.ChannelId = channelId;
            entry.ExpiresAt = now + _timeToLive;
        }

        PurgeExpired(now);
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            bool expired;
            lock (entry.Sync)
            {
                expired = entry.ExpiresAt <= now;
            }

            if (expired)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string InMemoryKey(string ownerUsername, string stickyKey)
    {
        return $"{ownerUsername.Trim()}\n{stickyKey}";
    }

    private sealed class Entry
    {
        public object Sync { get; } = new();

        public string ChannelId { get; set; } = string.Empty;

        public DateTimeOffset ExpiresAt { get; set; }
    }
}

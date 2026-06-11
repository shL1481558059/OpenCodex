using System.Collections.Concurrent;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 基于进程内内存的会话-渠道亲和映射服务，带滑动过期。
/// </summary>
/// <remarks>
/// 该实现仅在单进程内有效，不跨实例共享，进程重启后映射丢失。
/// 由于上游 prompt 缓存本身是短时效的，重建映射的代价可接受。
/// </remarks>
public sealed class ChannelAffinityService : IChannelAffinityService
{
    /// <summary>
    /// 映射的默认存活时长（滑动过期）。
    /// </summary>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeToLive;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// 使用默认存活时长与系统时钟初始化实例。
    /// </summary>
    public ChannelAffinityService()
        : this(DefaultTimeToLive, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// 使用指定存活时长与时钟初始化实例，主要用于测试注入。
    /// </summary>
    /// <param name="timeToLive">映射的滑动过期时长，必须为正值。</param>
    /// <param name="clock">当前时间提供器。</param>
    public ChannelAffinityService(TimeSpan timeToLive, Func<DateTimeOffset> clock)
    {
        _timeToLive = timeToLive > TimeSpan.Zero ? timeToLive : DefaultTimeToLive;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public string? GetPreferredChannelId(string ownerUsername, string stickyKey)
    {
        if (string.IsNullOrEmpty(stickyKey))
        {
            return null;
        }

        var key = Key(ownerUsername, stickyKey);
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

            // 滑动过期：读取也算一次活跃访问，延长有效期。
            entry.ExpiresAt = now + _timeToLive;
            return entry.ChannelId;
        }
    }

    /// <inheritdoc />
    public void Remember(string ownerUsername, string stickyKey, string channelId)
    {
        if (string.IsNullOrEmpty(stickyKey) || string.IsNullOrEmpty(channelId))
        {
            return;
        }

        var now = _clock();
        var key = Key(ownerUsername, stickyKey);
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

    private static string Key(string ownerUsername, string stickyKey)
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


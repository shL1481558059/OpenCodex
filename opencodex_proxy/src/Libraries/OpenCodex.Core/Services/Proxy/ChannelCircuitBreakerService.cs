using System.Collections.Concurrent;
using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.Caching;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using StackExchange.Redis;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 渠道熔断器。Redis 可用时跨实例共享熔断状态,不可用时降级为进程内。
/// </summary>
public sealed class ChannelCircuitBreakerService : IChannelCircuitBreakerService
{
    private const int DefaultFailureThreshold = 3;
    private static readonly TimeSpan DefaultOpenDuration = TimeSpan.FromSeconds(60);
    private const int DefaultHalfOpenMaxProbeRequests = 1;
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(5);
    private const int LockRetryCount = 3;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly int _halfOpenMaxProbeRequests;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IRedisConnectionProvider? _redis;

    public ChannelCircuitBreakerService()
        : this(
            DefaultFailureThreshold,
            DefaultOpenDuration,
            DefaultHalfOpenMaxProbeRequests,
            null,
            () => DateTimeOffset.UtcNow)
    {
    }

    public ChannelCircuitBreakerService(IRedisConnectionProvider? redis = null)
        : this(
            DefaultFailureThreshold,
            DefaultOpenDuration,
            DefaultHalfOpenMaxProbeRequests,
            redis,
            () => DateTimeOffset.UtcNow)
    {
    }

    public ChannelCircuitBreakerService(
        int failureThreshold,
        TimeSpan openDuration,
        int halfOpenMaxProbeRequests,
        IRedisConnectionProvider? redis,
        Func<DateTimeOffset> clock)
    {
        _failureThreshold = failureThreshold > 0 ? failureThreshold : DefaultFailureThreshold;
        _openDuration = openDuration > TimeSpan.Zero ? openDuration : DefaultOpenDuration;
        _halfOpenMaxProbeRequests = halfOpenMaxProbeRequests > 0
            ? halfOpenMaxProbeRequests
            : DefaultHalfOpenMaxProbeRequests;
        _redis = redis;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ChannelHealthStatus> GetHealthStatusAsync(string ownerUsername, string channelId, bool enabled, TimeSpan? openDurationOverride = null)
    {
        if (!enabled)
        {
            return ChannelHealthStatus.Disabled;
        }

        var openDuration = EffectiveOpenDuration(openDurationOverride);
        if (openDuration <= TimeSpan.Zero)
        {
            await ResetAsync(ownerUsername, channelId);
            return ChannelHealthStatus.Healthy;
        }

        var db = GetDatabase();
        if (db is not null)
        {
            return await GetHealthStatusRedisAsync(db, ownerUsername, channelId, openDuration);
        }

        if (!_entries.TryGetValue(Key(ownerUsername, channelId), out var entry))
        {
            return ChannelHealthStatus.Healthy;
        }

        lock (entry.Sync)
        {
            return RefreshState(entry, _clock(), openDuration);
        }
    }

    public async Task<bool> TryAcquireHalfOpenProbeAsync(string ownerUsername, string channelId, TimeSpan? openDurationOverride = null)
    {
        var openDuration = EffectiveOpenDuration(openDurationOverride);
        if (openDuration <= TimeSpan.Zero)
        {
            return false;
        }

        var db = GetDatabase();
        if (db is not null)
        {
            return await TryAcquireHalfOpenProbeRedisAsync(db, ownerUsername, channelId, openDuration);
        }

        var entry = _entries.GetOrAdd(Key(ownerUsername, channelId), static _ => new Entry());
        lock (entry.Sync)
        {
            if (RefreshState(entry, _clock(), openDuration) != ChannelHealthStatus.HalfOpen)
            {
                return false;
            }

            if (entry.HalfOpenProbeRequests >= _halfOpenMaxProbeRequests)
            {
                return false;
            }

            entry.HalfOpenProbeRequests++;
            return true;
        }
    }

    public async Task ReleaseHalfOpenProbeAsync(string ownerUsername, string channelId, TimeSpan? openDurationOverride = null)
    {
        var openDuration = EffectiveOpenDuration(openDurationOverride);
        if (openDuration <= TimeSpan.Zero)
        {
            _entries.TryRemove(Key(ownerUsername, channelId), out _);
            return;
        }

        var db = GetDatabase();
        if (db is not null)
        {
            await ReleaseHalfOpenProbeRedisAsync(db, ownerUsername, channelId, openDuration);
            return;
        }

        if (!_entries.TryGetValue(Key(ownerUsername, channelId), out var entry))
        {
            return;
        }

        lock (entry.Sync)
        {
            if (RefreshState(entry, _clock(), openDuration) != ChannelHealthStatus.HalfOpen)
            {
                return;
            }

            if (entry.HalfOpenProbeRequests > 0)
            {
                entry.HalfOpenProbeRequests--;
            }
        }
    }

    public async Task RecordSuccessAsync(string ownerUsername, string channelId)
    {
        var db = GetDatabase();
        if (db is not null)
        {
            await db.KeyDeleteAsync(BreakerKey(ownerUsername, channelId)).ConfigureAwait(false);
            return;
        }
        _entries.TryRemove(Key(ownerUsername, channelId), out _);
    }

    public async Task<bool> RecordFailureAsync(string ownerUsername, string channelId, Exception exception, TimeSpan? openDurationOverride = null)
    {
        if (!ShouldCountFailure(exception))
        {
            return false;
        }

        var openDuration = EffectiveOpenDuration(openDurationOverride);
        if (openDuration <= TimeSpan.Zero)
        {
            _entries.TryRemove(Key(ownerUsername, channelId), out _);
            return true;
        }

        var db = GetDatabase();
        if (db is not null)
        {
            return await RecordFailureRedisAsync(db, ownerUsername, channelId, openDuration);
        }

        var now = _clock();
        var entry = _entries.GetOrAdd(Key(ownerUsername, channelId), static _ => new Entry());
        lock (entry.Sync)
        {
            var state = RefreshState(entry, now, openDuration);
            if (state == ChannelHealthStatus.HalfOpen)
            {
                Open(entry, now, openDuration);
                return true;
            }

            if (state == ChannelHealthStatus.Open)
            {
                Open(entry, now, openDuration);
                return true;
            }

            entry.ConsecutiveFailures++;
            if (entry.ConsecutiveFailures >= _failureThreshold)
            {
                Open(entry, now, openDuration);
            }

            return true;
        }
    }

    public async Task ResetAsync(string ownerUsername, string channelId)
    {
        var db = GetDatabase();
        if (db is not null)
        {
            await db.KeyDeleteAsync(BreakerKey(ownerUsername, channelId)).ConfigureAwait(false);
            return;
        }
        _entries.TryRemove(Key(ownerUsername, channelId), out _);
    }

    private void Open(Entry entry, DateTimeOffset now, TimeSpan openDuration)
    {
        entry.ConsecutiveFailures = _failureThreshold;
        entry.HalfOpenProbeRequests = 0;
        entry.OpenedUntil = now + openDuration;
        entry.State = CircuitState.Open;
    }

    private TimeSpan EffectiveOpenDuration(TimeSpan? openDurationOverride)
    {
        if (!openDurationOverride.HasValue)
        {
            return _openDuration;
        }

        return openDurationOverride.Value;
    }

    private static bool ShouldCountFailure(Exception exception)
    {
        return exception is UpstreamException upstream
            && upstream.StatusCode is ProxyHttpStatus.BadRequest
                or ProxyHttpStatus.Forbidden
                or ProxyHttpStatus.TooManyRequests
                or ProxyHttpStatus.InternalServerError
                or ProxyHttpStatus.BadGateway
                or ProxyHttpStatus.GatewayTimeout
                or ProxyHttpStatus.ServiceUnavailable;
    }

    private static string Key(string ownerUsername, string channelId)
    {
        return $"{ownerUsername.Trim()}\n{channelId}";
    }

    private static ChannelHealthStatus RefreshState(Entry entry, DateTimeOffset now, TimeSpan openDuration)
    {
        if (openDuration <= TimeSpan.Zero)
        {
            entry.ConsecutiveFailures = 0;
            entry.HalfOpenProbeRequests = 0;
            entry.OpenedUntil = null;
            entry.State = CircuitState.Closed;
            return ChannelHealthStatus.Healthy;
        }

        if (entry.State == CircuitState.Open
            && entry.OpenedUntil is { } openedUntil
            && openedUntil > now)
        {
            return ChannelHealthStatus.Open;
        }

        if (entry.State == CircuitState.Open
            && entry.OpenedUntil is not null)
        {
            entry.OpenedUntil = null;
            entry.HalfOpenProbeRequests = 0;
            entry.State = CircuitState.HalfOpen;
            return ChannelHealthStatus.HalfOpen;
        }

        return entry.State switch
        {
            CircuitState.HalfOpen => ChannelHealthStatus.HalfOpen,
            _ => ChannelHealthStatus.Healthy
        };
    }

    // ===== Redis 路径 =====

    private IDatabase? GetDatabase()
    {
        return _redis is not null && _redis.IsAvailable ? _redis.GetDatabase() : null;
    }

    private async Task<ChannelHealthStatus> GetHealthStatusRedisAsync(
        IDatabase db, string ownerUsername, string channelId, TimeSpan openDuration)
    {
        var now = _clock();
        var (acquired, result) = await WithBreakerLockAsync(db, ownerUsername, channelId,
            async () =>
            {
                var snapshot = await ReadBreakerAsync(db, ownerUsername, channelId);
                var status = RefreshSnapshot(snapshot, now, openDuration);
                await WriteBreakerAsync(db, ownerUsername, channelId, snapshot, openDuration);
                return status;
            });

        if (acquired)
        {
            return result;
        }

        // 锁获取失败:降级读 Redis 无锁(状态可能瞬时不一致,但 TTL 兜底)
        var snap = await ReadBreakerAsync(db, ownerUsername, channelId);
        return RefreshSnapshot(snap, now, openDuration);
    }

    private async Task<bool> TryAcquireHalfOpenProbeRedisAsync(
        IDatabase db, string ownerUsername, string channelId, TimeSpan openDuration)
    {
        var now = _clock();
        var (acquired, result) = await WithBreakerLockAsync(db, ownerUsername, channelId,
            async () =>
            {
                var snapshot = await ReadBreakerAsync(db, ownerUsername, channelId);
                var status = RefreshSnapshot(snapshot, now, openDuration);
                if (status != ChannelHealthStatus.HalfOpen)
                {
                    await WriteBreakerAsync(db, ownerUsername, channelId, snapshot, openDuration);
                    return false;
                }

                if (snapshot.HalfOpenProbeRequests >= _halfOpenMaxProbeRequests)
                {
                    return false;
                }

                snapshot.HalfOpenProbeRequests++;
                await WriteBreakerAsync(db, ownerUsername, channelId, snapshot, openDuration);
                return true;
            });

        return acquired && result;
    }

    private async Task ReleaseHalfOpenProbeRedisAsync(
        IDatabase db, string ownerUsername, string channelId, TimeSpan openDuration)
    {
        var now = _clock();
        var (acquired, _) = await WithBreakerLockAsync(db, ownerUsername, channelId,
            async () =>
            {
                var snapshot = await ReadBreakerAsync(db, ownerUsername, channelId);
                var status = RefreshSnapshot(snapshot, now, openDuration);
                if (status != ChannelHealthStatus.HalfOpen)
                {
                    await WriteBreakerAsync(db, ownerUsername, channelId, snapshot, openDuration);
                    return true;
                }

                if (snapshot.HalfOpenProbeRequests > 0)
                {
                    snapshot.HalfOpenProbeRequests--;
                    await WriteBreakerAsync(db, ownerUsername, channelId, snapshot, openDuration);
                }
                return true;
            });

        // 锁失败:fire-and-forget 尝试(竞态无害,最坏少释放一次)
        if (!acquired)
        {
            var key = (RedisKey)BreakerKey(ownerUsername, channelId);
            await db.HashDecrementAsync(key, "probes", flags: CommandFlags.FireAndForget);
        }
    }

    private async Task<bool> RecordFailureRedisAsync(
        IDatabase db, string ownerUsername, string channelId, TimeSpan openDuration)
    {
        var now = _clock();
        var (acquired, result) = await WithBreakerLockAsync(db, ownerUsername, channelId,
            async () =>
            {
                var snapshot = await ReadBreakerAsync(db, ownerUsername, channelId);
                var status = RefreshSnapshot(snapshot, now, openDuration);

                if (status == ChannelHealthStatus.HalfOpen || status == ChannelHealthStatus.Open)
                {
                    OpenSnapshot(snapshot, now, openDuration);
                    await WriteBreakerAsync(db, ownerUsername, channelId, snapshot, openDuration);
                    return true;
                }

                snapshot.ConsecutiveFailures++;
                if (snapshot.ConsecutiveFailures >= _failureThreshold)
                {
                    OpenSnapshot(snapshot, now, openDuration);
                }
                await WriteBreakerAsync(db, ownerUsername, channelId, snapshot, openDuration);
                return true;
            });

        if (acquired)
        {
            return result;
        }

        // 锁失败:降级走进程内(多实例下可能与其他实例状态不一致,靠 TTL 兜底)
        var entry = _entries.GetOrAdd(Key(ownerUsername, channelId), static _ => new Entry());
        lock (entry.Sync)
        {
            var state = RefreshState(entry, now, openDuration);
            if (state == ChannelHealthStatus.HalfOpen || state == ChannelHealthStatus.Open)
            {
                Open(entry, now, openDuration);
                return true;
            }
            entry.ConsecutiveFailures++;
            if (entry.ConsecutiveFailures >= _failureThreshold)
            {
                Open(entry, now, openDuration);
            }
            return true;
        }
    }

    private async Task<(bool acquired, T? result)> WithBreakerLockAsync<T>(
        IDatabase db, string ownerUsername, string channelId, Func<Task<T>> action)
    {
        var lockKey = (RedisKey)BreakerLockKey(ownerUsername, channelId);
        var token = Guid.NewGuid().ToString("N");
        for (var attempt = 0; attempt < LockRetryCount; attempt++)
        {
            if (await db.LockTakeAsync(lockKey, token, LockTtl).ConfigureAwait(false))
            {
                try
                {
                    var result = await action().ConfigureAwait(false);
                    return (true, result);
                }
                finally
                {
                    db.LockRelease(lockKey, token, CommandFlags.FireAndForget);
                }
            }

            if (attempt < LockRetryCount - 1)
            {
                await Task.Delay(LockRetryDelay).ConfigureAwait(false);
            }
        }

        return (false, default);
    }

    private static async Task<BreakerSnapshot> ReadBreakerAsync(
        IDatabase db, string ownerUsername, string channelId)
    {
        var json = await db.StringGetAsync(BreakerKey(ownerUsername, channelId)).ConfigureAwait(false);
        if (!json.HasValue)
        {
            return new BreakerSnapshot();
        }
       try
       {
            return JsonSerializer.Deserialize<BreakerSnapshot>(json.ToString(), JsonOptions) ?? new BreakerSnapshot();
       }
        catch
        {
            return new BreakerSnapshot();
        }
    }

    private static async Task WriteBreakerAsync(
        IDatabase db, string ownerUsername, string channelId, BreakerSnapshot snapshot, TimeSpan ttl)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await db.StringSetAsync(
            BreakerKey(ownerUsername, channelId),
            json,
            ttl).ConfigureAwait(false);
    }

    private void OpenSnapshot(BreakerSnapshot snapshot, DateTimeOffset now, TimeSpan openDuration)
    {
        snapshot.ConsecutiveFailures = _failureThreshold;
        snapshot.HalfOpenProbeRequests = 0;
        snapshot.OpenedUntil = now.ToUnixTimeSeconds() + (long)openDuration.TotalSeconds;
        snapshot.State = (int)CircuitState.Open;
    }

    private static ChannelHealthStatus RefreshSnapshot(
        BreakerSnapshot snapshot, DateTimeOffset now, TimeSpan openDuration)
    {
        if (openDuration <= TimeSpan.Zero)
        {
            snapshot.ConsecutiveFailures = 0;
            snapshot.HalfOpenProbeRequests = 0;
            snapshot.OpenedUntil = null;
            snapshot.State = (int)CircuitState.Closed;
            return ChannelHealthStatus.Healthy;
        }

        if (snapshot.State == (int)CircuitState.Open
            && snapshot.OpenedUntil is { } openedUntil
            && openedUntil > now.ToUnixTimeSeconds())
        {
            return ChannelHealthStatus.Open;
        }

        if (snapshot.State == (int)CircuitState.Open
            && snapshot.OpenedUntil is not null)
        {
            snapshot.OpenedUntil = null;
            snapshot.HalfOpenProbeRequests = 0;
            snapshot.State = (int)CircuitState.HalfOpen;
            return ChannelHealthStatus.HalfOpen;
        }

        return snapshot.State switch
        {
            (int)CircuitState.HalfOpen => ChannelHealthStatus.HalfOpen,
            _ => ChannelHealthStatus.Healthy
        };
    }

    private static string BreakerKey(string ownerUsername, string channelId)
    {
        return $"breaker:{ownerUsername.Trim()}:{channelId}";
    }

    private static string BreakerLockKey(string ownerUsername, string channelId)
    {
        return $"breaker:lock:{ownerUsername.Trim()}:{channelId}";
    }

    private sealed class BreakerSnapshot
    {
        public int State { get; set; } // 0=Closed, 1=Open, 2=HalfOpen
        public int ConsecutiveFailures { get; set; }
        public int HalfOpenProbeRequests { get; set; }
        public long? OpenedUntil { get; set; } // Unix seconds
    }

    private sealed class Entry
    {
        public object Sync { get; } = new();

        public CircuitState State { get; set; }

        public int ConsecutiveFailures { get; set; }

        public int HalfOpenProbeRequests { get; set; }

        public DateTimeOffset? OpenedUntil { get; set; }
    }

    private enum CircuitState
    {
        Closed = 0,
        Open = 1,
        HalfOpen = 2
    }
}

using System.Collections.Concurrent;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 基于进程内内存的全局渠道熔断器。
/// </summary>
public sealed class ChannelCircuitBreakerService : IChannelCircuitBreakerService
{
    private const int DefaultFailureThreshold = 3;
    private static readonly TimeSpan DefaultOpenDuration = TimeSpan.FromSeconds(60);
    private const int DefaultHalfOpenMaxProbeRequests = 1;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly int _halfOpenMaxProbeRequests;
    private readonly Func<DateTimeOffset> _clock;

    public ChannelCircuitBreakerService()
        : this(
            DefaultFailureThreshold,
            DefaultOpenDuration,
            DefaultHalfOpenMaxProbeRequests,
            () => DateTimeOffset.UtcNow)
    {
    }

    public ChannelCircuitBreakerService(
        int failureThreshold,
        TimeSpan openDuration,
        int halfOpenMaxProbeRequests,
        Func<DateTimeOffset> clock)
    {
        _failureThreshold = failureThreshold > 0 ? failureThreshold : DefaultFailureThreshold;
        _openDuration = openDuration > TimeSpan.Zero ? openDuration : DefaultOpenDuration;
        _halfOpenMaxProbeRequests = halfOpenMaxProbeRequests > 0
            ? halfOpenMaxProbeRequests
            : DefaultHalfOpenMaxProbeRequests;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ChannelHealthStatus GetHealthStatus(string ownerUsername, string channelId, bool enabled, TimeSpan? openDurationOverride = null)
    {
        if (!enabled)
        {
            return ChannelHealthStatus.Disabled;
        }

        if (EffectiveOpenDuration(openDurationOverride) <= TimeSpan.Zero)
        {
            _entries.TryRemove(Key(ownerUsername, channelId), out _);
            return ChannelHealthStatus.Healthy;
        }

        if (!_entries.TryGetValue(Key(ownerUsername, channelId), out var entry))
        {
            return ChannelHealthStatus.Healthy;
        }

        lock (entry.Sync)
        {
            return RefreshState(entry, _clock(), EffectiveOpenDuration(openDurationOverride));
        }
    }

    public bool TryAcquireHalfOpenProbe(string ownerUsername, string channelId, TimeSpan? openDurationOverride = null)
    {
        if (EffectiveOpenDuration(openDurationOverride) <= TimeSpan.Zero)
        {
            return false;
        }

        var entry = _entries.GetOrAdd(Key(ownerUsername, channelId), static _ => new Entry());
        lock (entry.Sync)
        {
            if (RefreshState(entry, _clock(), EffectiveOpenDuration(openDurationOverride)) != ChannelHealthStatus.HalfOpen)
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

    public void ReleaseHalfOpenProbe(string ownerUsername, string channelId, TimeSpan? openDurationOverride = null)
    {
        if (EffectiveOpenDuration(openDurationOverride) <= TimeSpan.Zero)
        {
            _entries.TryRemove(Key(ownerUsername, channelId), out _);
            return;
        }

        if (!_entries.TryGetValue(Key(ownerUsername, channelId), out var entry))
        {
            return;
        }

        lock (entry.Sync)
        {
            if (RefreshState(entry, _clock(), EffectiveOpenDuration(openDurationOverride)) != ChannelHealthStatus.HalfOpen)
            {
                return;
            }

            if (entry.HalfOpenProbeRequests > 0)
            {
                entry.HalfOpenProbeRequests--;
            }
        }
    }

    public void RecordSuccess(string ownerUsername, string channelId)
    {
        _entries.TryRemove(Key(ownerUsername, channelId), out _);
    }

    public bool RecordFailure(string ownerUsername, string channelId, Exception exception, TimeSpan? openDurationOverride = null)
    {
        if (!ShouldCountFailure(exception))
        {
            return false;
        }

        var effectiveOpenDuration = EffectiveOpenDuration(openDurationOverride);
        if (effectiveOpenDuration <= TimeSpan.Zero)
        {
            _entries.TryRemove(Key(ownerUsername, channelId), out _);
            return true;
        }

        var now = _clock();
        var entry = _entries.GetOrAdd(Key(ownerUsername, channelId), static _ => new Entry());
        lock (entry.Sync)
        {
            var state = RefreshState(entry, now, effectiveOpenDuration);
            if (state == ChannelHealthStatus.HalfOpen)
            {
                Open(entry, now, effectiveOpenDuration);
                return true;
            }

            if (state == ChannelHealthStatus.Open)
            {
                Open(entry, now, effectiveOpenDuration);
                return true;
            }

            entry.ConsecutiveFailures++;
            if (entry.ConsecutiveFailures >= _failureThreshold)
            {
                Open(entry, now, effectiveOpenDuration);
            }

            return true;
        }
    }

    public void Reset(string ownerUsername, string channelId)
    {
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

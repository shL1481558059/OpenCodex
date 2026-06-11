using System.Collections.Concurrent;
using OpenCodex.Core.Config;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ChannelCapacityService : IChannelCapacityService
{
    private readonly ConcurrentDictionary<string, CounterEntry> _entries = new(StringComparer.Ordinal);

    public IChannelCapacityLease? TryAcquire(
        string ownerUsername,
        IReadOnlyDictionary<string, object?> channel)
    {
        var channelId = ChannelId(channel);
        var entry = _entries.GetOrAdd(Key(ownerUsername, channelId), static _ => new CounterEntry());
        lock (entry.Sync)
        {
            if (channel.TryGetValue("capacity", out var capacityValue)
                && capacityValue is int capacity
                && capacity > 0
                && entry.ActiveRequests >= capacity)
            {
                return null;
            }

            entry.ActiveRequests++;
        }

        return new Lease(this, ownerUsername, channelId);
    }

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

    private void Release(string ownerUsername, string channelId)
    {
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

            shouldRemove = entry.ActiveRequests == 0;
        }

        if (shouldRemove)
        {
            _entries.TryRemove(key, out _);
        }
    }

    private static string ChannelId(IReadOnlyDictionary<string, object?> channel)
    {
        return channel.TryGetValue("id", out var value)
            ? ConfigValue.PythonString(value).Trim()
            : string.Empty;
    }

    private static string Key(string ownerUsername, string channelId)
    {
        return $"{ownerUsername.Trim()}\n{channelId}";
    }

    private sealed class CounterEntry
    {
        public object Sync { get; } = new();

        public int ActiveRequests { get; set; }
    }

    private sealed class Lease : IChannelCapacityLease
    {
        private readonly ChannelCapacityService _owner;
        private readonly string _ownerUsername;
        private readonly string _channelId;
        private int _disposed;

        public Lease(ChannelCapacityService owner, string ownerUsername, string channelId)
        {
            _owner = owner;
            _ownerUsername = ownerUsername;
            _channelId = channelId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Release(_ownerUsername, _channelId);
        }
    }
}

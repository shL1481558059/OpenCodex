using System.Collections.Concurrent;
using OpenCodex.Core.Config;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ChannelCapacityService : IChannelCapacityService
{
    private readonly ConcurrentDictionary<string, CounterEntry> _entries = new(StringComparer.Ordinal);

    public IChannelCapacityLease? TryAcquire(
        string ownerUsername,
        IReadOnlyDictionary<string, object?> channel,
        string? requestModel = null,
        string? upstreamModel = null)
    {
        var channelId = ChannelId(channel);
        var modelUsageKey = new ModelUsageKey(CleanModel(requestModel), CleanModel(upstreamModel));
        var tracksModelUsage = modelUsageKey.Model is not null || modelUsageKey.UpstreamModel is not null;
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
            if (tracksModelUsage)
            {
                entry.ActiveModelRequests.TryGetValue(modelUsageKey, out var count);
                entry.ActiveModelRequests[modelUsageKey] = count + 1;
            }
        }

        return new Lease(this, ownerUsername, channelId, modelUsageKey, tracksModelUsage);
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
        bool tracksModelUsage)
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
        private int _disposed;

        public Lease(
            ChannelCapacityService owner,
            string ownerUsername,
            string channelId,
            ModelUsageKey modelUsageKey,
            bool tracksModelUsage)
        {
            _owner = owner;
            _ownerUsername = ownerUsername;
            _channelId = channelId;
            _modelUsageKey = modelUsageKey;
            _tracksModelUsage = tracksModelUsage;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Release(_ownerUsername, _channelId, _modelUsageKey, _tracksModelUsage);
        }
    }
}

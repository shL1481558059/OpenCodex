using System.Reflection;
using OpenCodex.Core.Services.Caching;
using StackExchange.Redis;

namespace OpenCodex.Api.Tests;

/// <summary>In-memory Redis stand-in with fault injection for native client cache tests.</summary>
internal sealed class RecordingRedisConnectionProvider : IRedisConnectionProvider
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public RecordingRedisConnectionProvider()
    {
        var database = DispatchProxy.Create<IDatabase, RedisDatabaseProxy>();
        ((RedisDatabaseProxy)(object)database).Provider = this;
        Database = database;
    }

    public IDatabase Database { get; }
    public bool IsAvailable { get; set; } = true;
    public string KeyPrefix => "test";
    public bool IgnoreDeletes { get; set; }
    public bool FailSets { get; set; }
    public bool ThrowOnSet { get; set; }
    public bool ThrowOnGet { get; set; }
    public bool ThrowOnDelete { get; set; }
    public bool StripGenerationOnRead { get; set; }
    public TimeSpan SetDelay { get; set; }
    public Task? DelaySetsUntil { get; set; }
    public int SetCount { get; private set; }
    public int DeleteCount { get; private set; }
    public int IgnoredDeleteCount { get; private set; }
    public int FailedSetCount { get; private set; }
    public Expiration? LastSetExpiry { get; private set; }
    public TimeSpan? LastRefreshExpiry { get; private set; }
    public IDatabase? GetDatabase(int db = -1) => IsAvailable ? Database : null;
    public ISubscriber? GetSubscriber() => null;

    public Dictionary<string, string> Snapshot()
    {
        lock (_gate) return new Dictionary<string, string>(_values, StringComparer.Ordinal);
    }

    public void Restore(IReadOnlyDictionary<string, string> values)
    {
        lock (_gate)
        {
            _values.Clear();
            foreach (var pair in values) _values[pair.Key] = pair.Value;
        }
    }

    public void SetRaw(string key, string value)
    {
        lock (_gate) _values[key] = value;
    }

    internal async Task<bool> SetAsync(string key, string value, Expiration expiry)
    {
        if (DelaySetsUntil is not null) await DelaySetsUntil;
        if (SetDelay > TimeSpan.Zero) await Task.Delay(SetDelay);
        if (ThrowOnSet) throw new RedisException("injected set failure");
        if (FailSets || !IsAvailable)
        {
            FailedSetCount++;
            return false;
        }
        lock (_gate) _values[key] = value;
        SetCount++;
        LastSetExpiry = expiry;
        return true;
    }

    internal Task<RedisValue> GetAsync(string key)
    {
        if (ThrowOnGet) throw new RedisException("injected get failure");
        if (!IsAvailable) return Task.FromResult(RedisValue.Null);
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var value)) return Task.FromResult(RedisValue.Null);
            if (StripGenerationOnRead) value = StripGeneration(value);
            return Task.FromResult((RedisValue)value);
        }
    }

    internal Task<bool> ExpireAsync(string key, TimeSpan? expiry)
    {
        LastRefreshExpiry = expiry;
        lock (_gate) return Task.FromResult(_values.ContainsKey(key));
    }

    internal Task<bool> DeleteAsync(string key)
    {
        if (ThrowOnDelete) throw new RedisException("injected delete failure");
        DeleteCount++;
        if (IgnoreDeletes || !IsAvailable)
        {
            IgnoredDeleteCount++;
            return Task.FromResult(false);
        }
        lock (_gate) return Task.FromResult(_values.Remove(key));
    }

    internal void Clear()
    {
        lock (_gate) _values.Clear();
    }

    private static string StripGeneration(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return json;
        var buffer = new System.Text.Json.Nodes.JsonObject();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name is "Generation" or "generation") continue;
            buffer[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
        }
        return buffer.ToJsonString();
    }
}

internal class RedisDatabaseProxy : DispatchProxy
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

internal sealed class TestClock : TimeProvider
{
    private long _ticks = DateTimeOffset.Parse("2026-01-01T00:00:00Z").UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(int seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
}

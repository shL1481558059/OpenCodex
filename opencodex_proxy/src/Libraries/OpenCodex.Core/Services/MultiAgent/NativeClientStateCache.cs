using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCodex.Core.Services.Caching;
using StackExchange.Redis;

namespace OpenCodex.Core.Services.MultiAgent;

public sealed class NativeClientStateCache
{
    private const int LogIntervalMilliseconds = 30_000;
    private readonly IRedisConnectionProvider? _redis;
    private readonly NativeClientStateCacheOptions _options;
    private readonly ILogger _logger;
    private readonly TimeSpan _ttl;
    private long _lastOversizeLog;
    private long _lastErrorLog;

    public NativeClientStateCache(IRedisConnectionProvider? redis, NativeClientStateCacheOptions? options = null,
        ILogger<NativeClientStateCache>? logger = null)
    {
        _redis = redis;
        _options = (options ?? new()).ValidatedCopy();
        _logger = logger ?? NullLogger<NativeClientStateCache>.Instance;
        _ttl = TimeSpan.FromHours(_options.RedisTtlHours);
    }

    internal Task SetSessionAsync(NativeClientCachedSession session, CancellationToken ct)
        => SetAsync(SessionKey(session.Owner, session.ThreadId), session, ct);

    internal Task<NativeClientCachedGroup?> GetGroupAsync(Guid owner, string rootThreadId, CancellationToken ct)
        => GetAsync<NativeClientCachedGroup>(
            GroupKey(owner, rootThreadId),
            value => value.Version == 1 && value.Generation > 0 && value.Owner == owner && value.RootThreadId == rootThreadId,
            refresh: false,
            ct);

    internal Task TouchGroupAsync(Guid owner, string rootThreadId, CancellationToken ct)
        => TouchAsync(GroupKey(owner, rootThreadId), ct);

    internal Task SetGroupAsync(NativeClientCachedGroup group, CancellationToken ct)
        => SetAsync(GroupKey(group.Owner, group.RootThreadId), group, ct);

    internal Task RemoveGroupAsync(Guid owner, string rootThreadId, CancellationToken ct)
        => DeleteAsync(GroupKey(owner, rootThreadId), ct);

    private async Task<T?> GetAsync<T>(string key, Func<T, bool> valid, bool refresh, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        var database = Database();
        if (database is null) return null;
        try
        {
            var raw = await database.StringGetAsync(key);
            if (!raw.HasValue) return null;
            var value = JsonSerializer.Deserialize<T>(raw.ToString());
            if (value is null || !valid(value))
            {
                await SafeDeleteAsync(database, key, ct);
                return null;
            }
            if (refresh) await SafeExpireAsync(database, key, ct);
            return value;
        }
        catch (RedisException exception)
        {
            LogError("get", key, exception);
            return null;
        }
        catch (TimeoutException exception)
        {
            LogError("get", key, exception);
            return null;
        }
        catch (JsonException exception)
        {
            LogError("get", key, exception);
            try { await database.KeyDeleteAsync(key); }
            catch (RedisException) { }
            catch (TimeoutException) { }
            return null;
        }
    }

    private Task SetAsync<T>(string key, T value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var database = Database();
        if (database is null) return Task.CompletedTask;
        var serialized = JsonSerializer.Serialize(value);
        var bytes = Encoding.UTF8.GetByteCount(serialized);
        if (bytes > _options.RedisMaxValueBytes)
        {
            LogOversize(key, bytes);
            return Task.CompletedTask;
        }
        return SafeSetAsync(database, key, serialized, ct);
    }

    private async Task SafeSetAsync(IDatabase database, string key, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await database.StringSetAsync(key, value, _ttl); }
        catch (RedisException exception) { LogError("set", key, exception); }
        catch (TimeoutException exception) { LogError("set", key, exception); }
    }

    private async Task TouchAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var database = Database();
        if (database is null) return;
        await SafeExpireAsync(database, key, ct);
    }

    private async Task SafeExpireAsync(IDatabase database, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await database.KeyExpireAsync(key, _ttl); }
        catch (RedisException exception) { LogError("expire", key, exception); }
        catch (TimeoutException exception) { LogError("expire", key, exception); }
    }

    private async Task DeleteAsync(string key, CancellationToken ct)
    {
        var database = Database();
        if (database is null) return;
        await SafeDeleteAsync(database, key, ct);
    }

    private async Task SafeDeleteAsync(IDatabase database, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await database.KeyDeleteAsync(key); }
        catch (RedisException exception) { LogError("delete", key, exception); }
        catch (TimeoutException exception) { LogError("delete", key, exception); }
    }

    private IDatabase? Database()
    {
        try { return _redis is { IsAvailable: true } ? _redis.GetDatabase() : null; }
        catch (RedisException exception)
        {
            LogError("connect", "", exception);
            return null;
        }
        catch (TimeoutException exception)
        {
            LogError("connect", "", exception);
            return null;
        }
    }

    private void LogOversize(string key, int bytes)
    {
        if (!ShouldLog(ref _lastOversizeLog)) return;
        _logger.LogWarning("Native client Redis cache skipped oversize value. key={Key} bytes={Bytes} limit={Limit}",
            key, bytes, _options.RedisMaxValueBytes);
    }

    private void LogError(string operation, string key, Exception exception)
    {
        if (!ShouldLog(ref _lastErrorLog)) return;
        _logger.LogWarning(exception, "Native client Redis cache {Operation} failed. key={Key}", operation, key);
    }

    private static bool ShouldLog(ref long last)
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref last);
        if (now - previous < LogIntervalMilliseconds && previous != 0) return false;
        return Interlocked.CompareExchange(ref last, now, previous) == previous;
    }

    private static string SessionKey(Guid owner, string threadId)
        => $"native-client:session:{owner:N}:{Hash(threadId)}";

    private static string GroupKey(Guid owner, string rootThreadId)
        => $"native-client:group:{owner:N}:{Hash(rootThreadId)}";

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed class NativeClientCachedSession
{
    public int Version { get; set; } = 1;
    public Guid Owner { get; set; }
    public string ThreadId { get; set; } = "";
    public string RootThreadId { get; set; } = "";
    public string ParentThreadId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public int MaxConcurrentSubagents { get; set; }
}

internal sealed class NativeClientCachedGroup
{
    public int Version { get; set; } = 1;
    public long Generation { get; set; }
    public Guid Owner { get; set; }
    public string RootThreadId { get; set; } = "";
    public int MaxConcurrentSubagents { get; set; }
    public List<NativeClientCachedSession> Sessions { get; set; } = [];
}

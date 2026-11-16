using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.Services.Caching;
using OpenCodex.CoreBase.Abstractions;
using StackExchange.Redis;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Core.Services.MultiAgent;

public sealed class NativeClientStateCache
{
    private readonly IRedisConnectionProvider? _redis;
    private readonly NativeClientStateCacheOptions _options;
    private readonly TimeSpan _ttl;

    public NativeClientStateCache(IRedisConnectionProvider? redis, NativeClientStateCacheOptions? options = null)
    {
        _redis = redis;
        _options = (options ?? new()).ValidatedCopy();
        _ttl = TimeSpan.FromHours(_options.RedisTtlHours);
    }

    internal Task<NativeClientCachedSession?> GetSessionAsync(Guid owner, string threadId, CancellationToken ct)
        => GetAsync<NativeClientCachedSession>(
            SessionKey(owner, threadId),
            value => value.Version == 1 && value.Owner == owner && value.ThreadId == threadId,
            ct);

    internal Task SetSessionAsync(NativeClientCachedSession session, CancellationToken ct)
        => SetAsync(SessionKey(session.Owner, session.ThreadId), session, ct);

    internal Task RemoveSessionAsync(Guid owner, string threadId, CancellationToken ct)
        => _redis?.GetDatabase() is { } database
            ? SafeDeleteAsync(database, SessionKey(owner, threadId), ct)
            : Task.CompletedTask;

    internal Task<NativeClientCachedGroup?> GetGroupAsync(Guid owner, string rootThreadId, CancellationToken ct)
        => GetAsync<NativeClientCachedGroup>(
            GroupKey(owner, rootThreadId),
            value => value.Version == 1 && value.Owner == owner && value.RootThreadId == rootThreadId,
            ct);

    internal Task SetGroupAsync(NativeClientCachedGroup group, CancellationToken ct)
        => SetAsync(GroupKey(group.Owner, group.RootThreadId), group, ct);

    internal Task RemoveGroupAsync(Guid owner, string rootThreadId, CancellationToken ct)
        => _redis?.GetDatabase() is { } database
            ? SafeDeleteAsync(database, GroupKey(owner, rootThreadId), ct)
            : Task.CompletedTask;

    internal Task<NativeClientCachedResponse?> GetResponseAsync(Guid owner, string threadId, string responseId, CancellationToken ct)
        => GetAsync<NativeClientCachedResponse>(
            ResponseKey(owner, threadId, responseId),
            value => value.Version == 1 && value.Owner == owner && value.ThreadId == threadId
                && value.ResponseId == responseId,
            ct);

    internal Task<NativeClientCachedResponse?> GetReplayAsync(Guid owner, string threadId, string replayKey, CancellationToken ct)
        => GetAsync<NativeClientCachedResponse>(
            ReplayKey(owner, threadId, replayKey),
            value => value.Version == 1 && value.Owner == owner && value.ThreadId == threadId
                && value.ReplayKey == replayKey,
            ct);

    internal Task SetResponseAsync(NativeClientCachedResponse response, CancellationToken ct)
        => SetResponseValueAsync(ResponseKey(response.Owner, response.ThreadId, response.ResponseId), response, ct);

    internal Task SetReplayAsync(NativeClientCachedResponse response, CancellationToken ct)
        => response.ReplayKey.Length == 0
            ? Task.CompletedTask
            : SetResponseValueAsync(ReplayKey(response.Owner, response.ThreadId, response.ReplayKey), response, ct);

    internal Task RemoveResponseAsync(Guid owner, string threadId, string responseId, CancellationToken ct)
        => _redis?.GetDatabase() is { } database
            ? SafeDeleteAsync(database, ResponseKey(owner, threadId, responseId), ct)
            : Task.CompletedTask;

    internal Task RemoveReplayAsync(Guid owner, string threadId, string replayKey, CancellationToken ct)
        => _redis?.GetDatabase() is { } database
            ? SafeDeleteAsync(database, ReplayKey(owner, threadId, replayKey), ct)
            : Task.CompletedTask;

    private async Task<T?> GetAsync<T>(string key, Func<T, bool> valid, CancellationToken ct) where T : class
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
                await database.KeyDeleteAsync(key);
                return null;
            }
            await database.KeyExpireAsync(key, _ttl);
            return value;
        }
        catch (RedisException) { return null; }
        catch (TimeoutException) { return null; }
        catch (JsonException)
        {
            try { await database.KeyDeleteAsync(key); }
            catch (RedisException) { }
            catch (TimeoutException) { }
            return null;
        }
    }

    private Task SetResponseValueAsync(string key, NativeClientCachedResponse response, CancellationToken ct)
        => SetAsync(key, response, ct);

    private Task SetAsync<T>(string key, T value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var database = Database();
        if (database is null) return Task.CompletedTask;
        var serialized = JsonSerializer.Serialize(value);
        var bytes = Encoding.UTF8.GetByteCount(serialized);
        if (bytes > _options.RedisMaxValueBytes) return Task.CompletedTask;
        return SafeSetAsync(database, key, serialized, ct);
    }

    private async Task SafeSetAsync(IDatabase database, string key, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await database.StringSetAsync(key, value, _ttl); }
        catch (RedisException) { }
        catch (TimeoutException) { }
    }

    private async Task SafeDeleteAsync(IDatabase database, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await database.KeyDeleteAsync(key); }
        catch (RedisException) { }
        catch (TimeoutException) { }
    }

    private IDatabase? Database()
    {
        try { return _redis is { IsAvailable: true } ? _redis.GetDatabase() : null; }
        catch (RedisException) { return null; }
        catch (TimeoutException) { return null; }
    }

    private static string SessionKey(Guid owner, string threadId)
        => $"native-client:session:{owner:N}:{Hash(threadId)}";

    private static string GroupKey(Guid owner, string rootThreadId)
        => $"native-client:group:{owner:N}:{Hash(rootThreadId)}";

    private static string ResponseKey(Guid owner, string threadId, string responseId)
        => $"native-client:response:{owner:N}:{Hash(threadId)}:{Hash(responseId)}";

    private static string ReplayKey(Guid owner, string threadId, string replayKey)
        => $"native-client:replay:{owner:N}:{Hash(threadId)}:{replayKey}";

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
    public Guid Owner { get; set; }
    public string RootThreadId { get; set; } = "";
    public int MaxConcurrentSubagents { get; set; }
    public List<NativeClientCachedSession> Sessions { get; set; } = [];
}

internal sealed class NativeClientCachedResponse
{
    public int Version { get; set; } = 1;
    public Guid Owner { get; set; }
    public string ThreadId { get; set; } = "";
    public string ResponseId { get; set; } = "";
    public string ReplayKey { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string ResponseJson { get; set; } = "";

    public D Payload() => Parse(PayloadJson);

    public D Response() => Parse(ResponseJson);

    private static D Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return (D)WebSearchPayload.FromJsonElement(document.RootElement)!;
    }
}

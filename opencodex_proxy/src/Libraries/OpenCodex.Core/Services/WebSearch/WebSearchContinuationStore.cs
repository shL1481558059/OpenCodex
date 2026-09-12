using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.Caching;
using OpenCodex.CoreBase.Domain.WebSearch;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

public sealed class WebSearchContinuationStore(IRedisConnectionProvider? redis = null) : IDisposable
{
    private readonly MemoryCache _memory = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public static string OwnerKey(string username, Guid? apiKeyId) => $"{username}\n{apiKeyId}";

    internal async Task SaveAsync(
        string owner,
        IReadOnlyList<WebSearchToolResult> results,
        IReadOnlyList<WebSearchToolCall> clientCalls,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }
        var searches = results.Select(result => (object?)WebSearchResponsePayload.BuildWebSearchItem(result, true)).ToList();
        var entries = results.ToDictionary(result => result.ItemId, result => JsonDumps(new Dictionary<string, object?>
        {
            ["searches"] = new List<object?> { WebSearchResponsePayload.BuildWebSearchItem(result, true) }
        }), StringComparer.Ordinal);
        foreach (var call in clientCalls)
        {
            entries[$"client:{call.Id}"] = JsonDumps(new Dictionary<string, object?> { ["searches"] = searches });
        }
        foreach (var (identifier, json) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = CacheKey(owner, identifier);
            if (redis?.IsAvailable is true)
            {
                try
                {
                    var database = redis.GetDatabase()
                        ?? throw new ProxyException("web search continuation store is unavailable", ProxyHttpStatus.ServiceUnavailable);
                    await database.StringSetAsync(key, json, Lifetime).WaitAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new ProxyException("failed to persist web search continuation", ProxyHttpStatus.ServiceUnavailable);
                }
            }
            _memory.Set(key, json, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = Lifetime,
                Size = Encoding.UTF8.GetByteCount(json)
            });
        }
    }

    public async Task RestoreAsync(
        Dictionary<string, object?> payload,
        string owner,
        string? internalName,
        CancellationToken cancellationToken)
    {
        if (!TryAsList(GetValue(payload, "input"), out var input))
        {
            return;
        }
        var hasManagedItems = input.OfType<Dictionary<string, object?>>().Any(IsManagedSearch);
        if (!hasManagedItems && internalName is null)
        {
            return;
        }
        var presentIds = input.OfType<Dictionary<string, object?>>()
            .Where(item => StringValue(item, "type") == "web_search_call")
            .Select(item => StringValue(item, "id")).ToHashSet(StringComparer.Ordinal);
        var injected = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<object?>();
        var pendingResults = new List<object?>();

        void AppendSearch(Dictionary<string, object?> item)
        {
            var id = StringValue(item, "id");
            if (!injected.Add(id))
            {
                return;
            }
            output.Add(new Dictionary<string, object?>
            {
                ["type"] = "function_call",
                ["call_id"] = id,
                ["name"] = internalName ?? WebSearchRequestPolicy.InternalToolName,
                ["arguments"] = JsonDumps(new Dictionary<string, object?>
                {
                    ["query"] = GetValue(ObjectValue(item, "action"), "query")
                })
            });
            pendingResults.Add(new Dictionary<string, object?>
            {
                ["type"] = "function_call_output",
                ["call_id"] = id,
                ["output"] = JsonDumps(GetValue(item, "opencodex_result"))
            });
        }

        foreach (var value in input)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAsObject(value, out var item))
            {
                if (IsManagedSearch(item))
                {
                    var id = StringValue(item, "id");
                    var snapshot = await ReadAsync(owner, id, cancellationToken);
                    var stored = ListValue(snapshot ?? [], "searches").OfType<Dictionary<string, object?>>()
                        .FirstOrDefault(search => StringValue(search, "id") == id);
                    var search = stored ?? (item.ContainsKey("opencodex_result") ? item : null);
                    if (search is null)
                    {
                        throw new BadRequestException("web search continuation expired; replay the complete search result");
                    }
                    AppendSearch(search);
                    continue;
                }

                var callId = StringValue(item, "call_id");
                var itemType = StringValue(item, "type");
                if (internalName is not null && callId.Length > 0
                    && (itemType.EndsWith("_call", StringComparison.Ordinal) || itemType == "function_call_output"))
                {
                    var snapshot = await ReadAsync(owner, $"client:{callId}", cancellationToken);
                    foreach (var search in ListValue(snapshot ?? [], "searches").OfType<Dictionary<string, object?>>())
                    {
                        if (!presentIds.Contains(StringValue(search, "id")))
                        {
                            AppendSearch(search);
                        }
                    }
                }
            }

            if (!TryAsObject(value, out var callItem)
                || !StringValue(callItem, "type").EndsWith("_call", StringComparison.Ordinal))
            {
                output.AddRange(pendingResults);
                pendingResults.Clear();
            }
            output.Add(DeepCopy(value));
        }
        output.AddRange(pendingResults);
        payload["input"] = output;
    }

    private static bool IsManagedSearch(Dictionary<string, object?> item) =>
        StringValue(item, "type") == "web_search_call"
        && (StringValue(item, "id").StartsWith(WebSearchRequestPolicy.PublicItemPrefix, StringComparison.Ordinal)
            || item.ContainsKey("opencodex_result"));

    private async Task<Dictionary<string, object?>?> ReadAsync(
        string owner, string identifier, CancellationToken cancellationToken)
    {
        var key = CacheKey(owner, identifier);
        if (!_memory.TryGetValue(key, out string? json) && redis?.IsAvailable is true)
        {
            try
            {
                var database = redis.GetDatabase()
                    ?? throw new ProxyException("web search continuation store is unavailable", ProxyHttpStatus.ServiceUnavailable);
                var value = await database.StringGetAsync(key).WaitAsync(cancellationToken);
                json = value.HasValue ? value.ToString() : null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new ProxyException("failed to read web search continuation", ProxyHttpStatus.ServiceUnavailable);
            }
        }
        if (json is null)
        {
            return null;
        }
        using var document = JsonDocument.Parse(json);
        return (Dictionary<string, object?>)FromJsonElement(document.RootElement)!;
    }

    private static string CacheKey(string owner, string identifier) =>
        $"web-search-continuation:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{identifier}")))}";

    public void Dispose() => _memory.Dispose();
}

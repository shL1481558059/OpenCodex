using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.WebSearch;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

public sealed class WebSearchContinuationStore(
    IWebSearchContinuationRepository repository,
    ILogger<WebSearchContinuationStore> logger)
{
    private const int PayloadVersion = 1;
    private const string SearchKind = "search";
    private const string ClientRoundKind = "client-round";
    private const string UnavailableError = "The previous web search result is unavailable.";

    public async Task SaveAsync(
        Guid ownerUserId,
        IReadOnlyList<WebSearchToolResult> results,
        IReadOnlyList<WebSearchToolCall> clientCalls,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }

        var createdAt = UnixTimeSeconds();
        var entries = new List<WebSearchContinuationEntry>(results.Count + clientCalls.Count);
        var searchIds = results.Select(result => result.ItemId).ToList();
        foreach (var result in results)
        {
            var item = WebSearchResponsePayload.BuildWebSearchItem(result, true);
            entries.Add(new WebSearchContinuationEntry
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                EntryKey = result.ItemId,
                Kind = SearchKind,
                PayloadVersion = PayloadVersion,
                PayloadJson = JsonDumps(new Dictionary<string, object?> { ["search"] = item }),
                CreatedAt = createdAt
            });
        }

        foreach (var call in clientCalls)
        {
            entries.Add(new WebSearchContinuationEntry
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                EntryKey = ClientEntryKey(call.Id),
                Kind = ClientRoundKind,
                PayloadVersion = PayloadVersion,
                PayloadJson = JsonDumps(new Dictionary<string, object?>
                {
                    ["search_ids"] = searchIds.Select(id => (object?)id).ToList()
                }),
                CreatedAt = createdAt
            });
        }

        try
        {
            await repository.UpsertAsync(entries, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist web search continuation data.");
            throw new ProxyException(
                "failed to persist web search continuation",
                ProxyHttpStatus.ServiceUnavailable);
        }
    }

    public async Task RestoreAsync(
        Dictionary<string, object?> payload,
        Guid ownerUserId,
        string? internalName,
        CancellationToken cancellationToken)
    {
        if (!TryAsList(GetValue(payload, "input"), out var input))
        {
            return;
        }

        var inputObjects = input.OfType<Dictionary<string, object?>>().ToList();
        var hasManagedItems = inputObjects.Any(IsManagedSearch);
        if (!hasManagedItems && internalName is null)
        {
            return;
        }

        var managedSearchIds = inputObjects
            .Where(IsManagedSearch)
            .Select(item => StringValue(item, "id"))
            .Where(id => id.Length > 0)
            .ToList();
        var clientKeys = internalName is null
            ? []
            : inputObjects
                .Where(IsClientContinuationReference)
                .Select(item => ClientEntryKey(StringValue(item, "call_id")))
                .Where(key => key.Length > ClientEntryPrefix.Length)
                .ToList();

        var entries = await LoadAsync(ownerUserId, managedSearchIds.Concat(clientKeys), cancellationToken);
        var referencedSearchIds = entries.Values
            .Where(entry => entry.Kind == ClientRoundKind)
            .SelectMany(ParseClientSearchIds)
            .Where(id => !entries.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (referencedSearchIds.Count > 0)
        {
            var referenced = await LoadAsync(ownerUserId, referencedSearchIds, cancellationToken);
            foreach (var (key, entry) in referenced)
            {
                entries[key] = entry;
            }
        }

        var storedSearches = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var entry in entries.Values.Where(entry => entry.Kind == SearchKind))
        {
            var search = ParseSearch(entry);
            if (search is not null)
            {
                storedSearches[StringValue(search, "id")] = search;
            }
        }

        var presentIds = inputObjects
            .Where(item => StringValue(item, "type") == "web_search_call")
            .Select(item => StringValue(item, "id"))
            .ToHashSet(StringComparer.Ordinal);
        var injected = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<object?>();
        var pendingResults = new List<object?>();
        var recovered = new List<WebSearchContinuationEntry>();

        void AppendSearch(Dictionary<string, object?> item)
        {
            var id = StringValue(item, "id");
            if (id.Length == 0 || !injected.Add(id))
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
                    if (!storedSearches.TryGetValue(id, out var search))
                    {
                        if (item.ContainsKey("opencodex_result"))
                        {
                            search = item;
                            recovered.Add(BuildSearchEntry(ownerUserId, item));
                        }
                        else
                        {
                            search = BuildUnavailableSearch(item);
                            logger.LogWarning(
                                "Web search continuation {EntryKey} was not found for owner {OwnerUserId}; restoring an unavailable result.",
                                id,
                                ownerUserId);
                        }
                    }

                    AppendSearch(search);
                    continue;
                }

                var callId = StringValue(item, "call_id");
                if (internalName is not null && callId.Length > 0 && IsClientContinuationReference(item))
                {
                    var key = ClientEntryKey(callId);
                    if (entries.TryGetValue(key, out var clientRound))
                    {
                        foreach (var searchId in ParseClientSearchIds(clientRound))
                        {
                            if (!presentIds.Contains(searchId) && storedSearches.TryGetValue(searchId, out var search))
                            {
                                AppendSearch(search);
                            }
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

        if (recovered.Count > 0)
        {
            try
            {
                await repository.UpsertAsync(recovered, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to repair web search continuation data from the client payload.");
            }
        }
    }

    private static string ClientEntryPrefix => "client:";

    private static string ClientEntryKey(string callId) => $"{ClientEntryPrefix}{callId}";

    private static bool IsClientContinuationReference(Dictionary<string, object?> item)
    {
        var itemType = StringValue(item, "type");
        return StringValue(item, "call_id").Length > 0
            && (itemType.EndsWith("_call", StringComparison.Ordinal) || itemType == "function_call_output");
    }

    private async Task<Dictionary<string, WebSearchContinuationEntry>> LoadAsync(
        Guid ownerUserId,
        IEnumerable<string> entryKeys,
        CancellationToken cancellationToken)
    {
        var keys = entryKeys.Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
        {
            return new Dictionary<string, WebSearchContinuationEntry>(StringComparer.Ordinal);
        }

        try
        {
            var loaded = await repository.LoadAsync(ownerUserId, keys, cancellationToken);
            return new Dictionary<string, WebSearchContinuationEntry>(loaded, StringComparer.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read web search continuation data.");
            throw new ProxyException(
                "failed to read web search continuation",
                ProxyHttpStatus.ServiceUnavailable);
        }
    }

    private static bool IsManagedSearch(Dictionary<string, object?> item) =>
        StringValue(item, "type") == "web_search_call"
        && (StringValue(item, "id").StartsWith(WebSearchRequestPolicy.PublicItemPrefix, StringComparison.Ordinal)
            || item.ContainsKey("opencodex_result"));

    private static WebSearchContinuationEntry BuildSearchEntry(
        Guid ownerUserId,
        Dictionary<string, object?> item) =>
        new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            EntryKey = StringValue(item, "id"),
            Kind = SearchKind,
            PayloadVersion = PayloadVersion,
            PayloadJson = JsonDumps(new Dictionary<string, object?> { ["search"] = DeepCopyObject(item) }),
            CreatedAt = UnixTimeSeconds()
        };

    private static Dictionary<string, object?>? ParseSearch(WebSearchContinuationEntry entry)
    {
        if (entry.PayloadVersion != PayloadVersion)
        {
            return null;
        }

        var payload = ParsePayload(entry.PayloadJson);
        return payload is not null && TryAsObject(GetValue(payload, "search"), out var search)
            ? search
            : null;
    }

    private static IReadOnlyList<string> ParseClientSearchIds(WebSearchContinuationEntry entry)
    {
        if (entry.PayloadVersion != PayloadVersion)
        {
            return [];
        }

        var payload = ParsePayload(entry.PayloadJson);
        return payload is null
            ? []
            : ListValue(payload, "search_ids").OfType<string>().Where(id => id.Length > 0).ToList();
    }

    private static Dictionary<string, object?>? ParsePayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return (Dictionary<string, object?>)FromJsonElement(document.RootElement)!;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> BuildUnavailableSearch(Dictionary<string, object?> item)
    {
        var action = ObjectValue(item, "action");
        var sources = ListValue(action, "sources")
            .OfType<Dictionary<string, object?>>()
            .Where(source => StringValue(source, "url").Length > 0)
            .Select(source => (object?)new Dictionary<string, object?>
            {
                ["title"] = StringValue(source, "title", StringValue(source, "url")),
                ["url"] = StringValue(source, "url"),
                ["content"] = string.Empty
            })
            .ToList();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = StringValue(item, "id"),
            ["type"] = "web_search_call",
            ["status"] = "failed",
            ["action"] = DeepCopyObject(action),
            ["opencodex_result"] = new Dictionary<string, object?>
            {
                ["answer"] = string.Empty,
                ["results"] = sources,
                ["error"] = UnavailableError
            }
        };
    }

    private static double UnixTimeSeconds() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}

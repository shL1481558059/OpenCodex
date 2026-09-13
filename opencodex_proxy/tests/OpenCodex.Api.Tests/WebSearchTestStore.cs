using Microsoft.Extensions.Logging.Abstractions;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Data;

namespace OpenCodex.Api.Tests;

internal sealed class InMemoryWebSearchContinuationRepository : IWebSearchContinuationRepository
{
    private readonly object _sync = new();
    private readonly Dictionary<(Guid OwnerUserId, string EntryKey), WebSearchContinuationEntry> _entries = [];

    public Task UpsertAsync(
        IReadOnlyCollection<WebSearchContinuationEntry> entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            foreach (var entry in entries)
            {
                _entries[(entry.OwnerUserId, entry.EntryKey)] = Copy(entry);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, WebSearchContinuationEntry>> LoadAsync(
        Guid ownerUserId,
        IReadOnlyCollection<string> entryKeys,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var result = new Dictionary<string, WebSearchContinuationEntry>(StringComparer.Ordinal);
            foreach (var key in entryKeys)
            {
                if (_entries.TryGetValue((ownerUserId, key), out var entry))
                {
                    result[key] = Copy(entry);
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, WebSearchContinuationEntry>>(result);
        }
    }

    public int DeleteAll()
    {
        lock (_sync)
        {
            var count = _entries.Count;
            _entries.Clear();
            return count;
        }
    }

    private static WebSearchContinuationEntry Copy(WebSearchContinuationEntry entry) =>
        new()
        {
            Id = entry.Id,
            OwnerUserId = entry.OwnerUserId,
            EntryKey = entry.EntryKey,
            Kind = entry.Kind,
            PayloadVersion = entry.PayloadVersion,
            PayloadJson = entry.PayloadJson,
            CreatedAt = entry.CreatedAt
        };
}

internal static class WebSearchTestStore
{
    public static readonly Guid OwnerUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static WebSearchContinuationStore Create() =>
        new(new InMemoryWebSearchContinuationRepository(), NullLogger<WebSearchContinuationStore>.Instance);
}

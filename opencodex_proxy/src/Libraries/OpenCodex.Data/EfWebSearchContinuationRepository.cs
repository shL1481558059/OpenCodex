using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Data;

namespace OpenCodex.Data;

public sealed class EfWebSearchContinuationRepository(IOpenCodexDbContext context) : IWebSearchContinuationRepository
{
    private const int BatchSize = 200;

    public async Task UpsertAsync(
        IReadOnlyCollection<WebSearchContinuationEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        foreach (var ownerGroup in entries.GroupBy(entry => entry.OwnerUserId))
        {
            var ownerEntries = ownerGroup.ToList();
            foreach (var batch in Chunk(ownerEntries, BatchSize))
            {
                var keys = batch.Select(entry => entry.EntryKey).ToHashSet(StringComparer.Ordinal);
                var existing = await context.WebSearchContinuationEntries
                    .Where(entry => entry.OwnerUserId == ownerGroup.Key && keys.Contains(entry.EntryKey))
                    .ToDictionaryAsync(entry => entry.EntryKey, StringComparer.Ordinal, cancellationToken);

                foreach (var entry in batch)
                {
                    if (!existing.TryGetValue(entry.EntryKey, out var stored))
                    {
                        context.WebSearchContinuationEntries.Add(entry);
                        continue;
                    }

                    stored.Kind = entry.Kind;
                    stored.PayloadVersion = entry.PayloadVersion;
                    stored.PayloadJson = entry.PayloadJson;
                }
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, WebSearchContinuationEntry>> LoadAsync(
        Guid ownerUserId,
        IReadOnlyCollection<string> entryKeys,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, WebSearchContinuationEntry>(StringComparer.Ordinal);
        if (entryKeys.Count == 0)
        {
            return result;
        }

        foreach (var batch in Chunk(entryKeys.Distinct(StringComparer.Ordinal).ToList(), BatchSize))
        {
            var stored = await context.WebSearchContinuationEntries
                .AsNoTracking()
                .Where(entry => entry.OwnerUserId == ownerUserId && batch.Contains(entry.EntryKey))
                .ToListAsync(cancellationToken);
            foreach (var entry in stored)
            {
                result[entry.EntryKey] = entry;
            }
        }

        return result;
    }

    public int DeleteAll() => context.WebSearchContinuationEntries.ExecuteDelete();

    private static IEnumerable<List<T>> Chunk<T>(List<T> values, int size)
    {
        for (var index = 0; index < values.Count; index += size)
        {
            yield return values.GetRange(index, Math.Min(size, values.Count - index));
        }
    }
}

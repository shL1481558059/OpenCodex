using OpenCodex.Core.Domain;

namespace OpenCodex.CoreBase.Data;

public interface IWebSearchContinuationRepository
{
    Task UpsertAsync(
        IReadOnlyCollection<WebSearchContinuationEntry> entries,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, WebSearchContinuationEntry>> LoadAsync(
        Guid ownerUserId,
        IReadOnlyCollection<string> entryKeys,
        CancellationToken cancellationToken);

    int DeleteAll();
}

namespace OpenCodex.CoreBase.Abstractions;

public interface IProxyStreamWriter
{
    void PrepareSse();

    Task<int?> WriteLinesAsync(
        IAsyncEnumerable<string> lines,
        Func<string, bool> countsForTtft,
        Func<int> elapsedMilliseconds,
        CancellationToken cancellationToken = default);
}

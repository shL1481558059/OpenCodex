namespace OpenCodex.CoreBase.Abstractions;

public sealed class TrackingProxyStreamWriter : IProxyStreamWriter
{
    private readonly IProxyStreamWriter _inner;

    public TrackingProxyStreamWriter(IProxyStreamWriter inner)
    {
        _inner = inner;
    }

    public bool HasWritten { get; private set; }

    public void PrepareSse()
    {
        _inner.PrepareSse();
    }

    public async Task<StreamWriteMetrics> WriteLinesAsync(
        IAsyncEnumerable<string> lines,
        Func<string, bool> countsForTtft,
        Func<int> elapsedMilliseconds,
        CancellationToken cancellationToken = default)
    {
        async IAsyncEnumerable<string> TrackWrites(
            IAsyncEnumerable<string> source,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var line in source.WithCancellation(token))
            {
                HasWritten = true;
                yield return line;
            }
        }

        return await _inner.WriteLinesAsync(
            TrackWrites(lines, cancellationToken),
            countsForTtft,
            elapsedMilliseconds,
            cancellationToken);
    }
}

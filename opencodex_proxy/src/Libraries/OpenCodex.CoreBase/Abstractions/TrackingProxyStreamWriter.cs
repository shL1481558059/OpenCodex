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
        if (IsPrepared)
        {
            return;
        }

        _inner.PrepareSse();
        IsPrepared = true;
    }

    /// <summary>
    /// SSE 响应头是否已准备（<see cref="PrepareSse"/> 是否已被调用）。
    /// </summary>
    public bool IsPrepared { get; private set; }

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
                if (!IsPrepared)
                {
                    PrepareSse();
                }

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

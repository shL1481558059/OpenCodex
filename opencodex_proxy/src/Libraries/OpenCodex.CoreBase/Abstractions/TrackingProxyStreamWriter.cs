using OpenCodex.Core.Protocols;

namespace OpenCodex.CoreBase.Abstractions;

public sealed class TrackingProxyStreamWriter : IProxyStreamWriter
{
    private readonly IProxyStreamWriter _inner;

    public TrackingProxyStreamWriter(
        IProxyStreamWriter inner,
        bool countOnlyMeaningfulWrites = false)
    {
        _inner = inner;
        _ = countOnlyMeaningfulWrites; // Retained parameter for source compatibility; semantic commitment is universal.
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

    public Task<StreamWriteMetrics> WriteLinesAsync(
        IAsyncEnumerable<string> lines,Func<string,bool> countsForTtft,Func<int> elapsedMilliseconds,
        CancellationToken cancellationToken=default) =>
        WriteFramesAsync(SseFrameReader.ReadLines(lines,cancellationToken:cancellationToken),countsForTtft,elapsedMilliseconds,cancellationToken);

    public Task<StreamWriteMetrics> WriteFramesAsync(
        IAsyncEnumerable<SseFrame> frames, Func<string, bool> countsForTtft,
        Func<int> elapsedMilliseconds, CancellationToken cancellationToken = default)
    {
        return _inner.WriteFramesAsync(Track(frames, cancellationToken), countsForTtft, elapsedMilliseconds, cancellationToken);

        async IAsyncEnumerable<SseFrame> Track(IAsyncEnumerable<SseFrame> source,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var frame in source.WithCancellation(token))
            {
                if (!IsPrepared) PrepareSse();
                if (frame.IsSemantic) HasWritten = true;
                yield return frame;
            }
        }
    }

}

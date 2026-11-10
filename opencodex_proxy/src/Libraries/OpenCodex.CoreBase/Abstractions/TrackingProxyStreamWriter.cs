using OpenCodex.Core.Protocols;

namespace OpenCodex.CoreBase.Abstractions;

public sealed class TrackingProxyStreamWriter : IProxyStreamWriter
{
    private readonly IProxyStreamWriter _inner;
    private readonly bool _countOnlyMeaningfulWrites;

    public TrackingProxyStreamWriter(
        IProxyStreamWriter inner,
        bool countOnlyMeaningfulWrites = false)
    {
        _inner = inner;
        _countOnlyMeaningfulWrites = countOnlyMeaningfulWrites;
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

                if (!_countOnlyMeaningfulWrites || IsMeaningfulLine(line))
                {
                    HasWritten = true;
                }

                yield return line;
            }
        }

        return await _inner.WriteLinesAsync(
            TrackWrites(lines, cancellationToken),
            countsForTtft,
            elapsedMilliseconds,
            cancellationToken);
    }

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
                if (!_countOnlyMeaningfulWrites || IsMeaningfulLine(frame.WireText)) HasWritten = true;
                yield return frame;
            }
        }
    }

    private static bool IsMeaningfulLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return !line.Contains("event: response.created", StringComparison.Ordinal)
            && !line.Contains("event: response.in_progress", StringComparison.Ordinal);
    }
}

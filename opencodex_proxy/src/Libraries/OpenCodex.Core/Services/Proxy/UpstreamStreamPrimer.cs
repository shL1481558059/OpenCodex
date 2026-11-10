using System.Runtime.CompilerServices;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 在协议转换前先确认上游流已产出首行，避免上游立即失败时下游先发出合成骨架事件。
/// </summary>
internal static class UpstreamStreamPrimer
{
    public static async Task<IAsyncEnumerable<T>> PrimeAsync<T>(
        IAsyncEnumerable<T> lines,
        CancellationToken cancellationToken)
    {
        var enumerator = lines.GetAsyncEnumerator(cancellationToken);
        bool hasFirstLine;
        try
        {
            hasFirstLine = await enumerator.MoveNextAsync();
        }
        catch
        {
            await enumerator.DisposeAsync();
            throw;
        }

        if (!hasFirstLine)
        {
            await enumerator.DisposeAsync();
            return EmptyStreamLines<T>(cancellationToken);
        }

        return ReplayPrimedStreamLines<T>(
            enumerator.Current,
            enumerator,
            cancellationToken);
    }

    private static async IAsyncEnumerable<T> EmptyStreamLines<T>(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<T> ReplayPrimedStreamLines<T>(
        T firstLine,
        IAsyncEnumerator<T> enumerator,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return firstLine;

            while (await enumerator.MoveNextAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}

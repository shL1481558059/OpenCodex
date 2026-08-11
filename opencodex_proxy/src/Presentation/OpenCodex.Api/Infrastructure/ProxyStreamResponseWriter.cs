using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Api.Infrastructure;

public sealed class ProxyStreamResponseWriter : IProxyStreamWriter
{
    private readonly HttpResponse _response;

    public ProxyStreamResponseWriter(HttpResponse response)
    {
        _response = response;
    }

    public void PrepareSse()
    {
        PrepareSse(_response);
    }

    public Task<StreamWriteMetrics> WriteLinesAsync(
        IAsyncEnumerable<string> lines,
        Func<string, bool> countsForTtft,
        Func<int> elapsedMilliseconds,
        CancellationToken cancellationToken = default)
    {
        return WriteLinesAsync(
            _response,
            lines,
            countsForTtft,
            elapsedMilliseconds,
            cancellationToken);
    }

    public static void PrepareSse(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    public static async Task<StreamWriteMetrics> WriteLinesAsync(
        HttpResponse response,
        IAsyncEnumerable<string> lines,
        Func<string, bool> countsForTtft,
        Func<int> elapsedMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var metrics = new StreamWriteMetrics();
        var sawCompleted = false;
        var sawDone = false;
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            if (metrics.TtftMs is null && countsForTtft(line))
            {
                metrics.TtftMs = elapsedMilliseconds();
            }

            if (!sawCompleted && line.Contains("response.completed", StringComparison.Ordinal))
            {
                sawCompleted = true;
            }

            if (!sawDone && line.Contains("data: [DONE]", StringComparison.Ordinal))
            {
                sawDone = true;
            }

            await response.WriteAsync(line, cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }

        if (sawCompleted && !sawDone)
        {
            await response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }

        return metrics;
    }
}

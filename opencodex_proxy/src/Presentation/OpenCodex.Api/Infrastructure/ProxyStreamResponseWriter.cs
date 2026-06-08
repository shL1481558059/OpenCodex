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

    public Task<int?> WriteLinesAsync(
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

    public static async Task<int?> WriteLinesAsync(
        HttpResponse response,
        IAsyncEnumerable<string> lines,
        Func<string, bool> countsForTtft,
        Func<int> elapsedMilliseconds,
        CancellationToken cancellationToken = default)
    {
        int? ttftMs = null;
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            if (ttftMs is null && countsForTtft(line))
            {
                ttftMs = elapsedMilliseconds();
            }

            await response.WriteAsync(line, cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }

        return ttftMs;
    }
}

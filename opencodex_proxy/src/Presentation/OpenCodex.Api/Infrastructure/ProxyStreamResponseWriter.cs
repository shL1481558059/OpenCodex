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
            int? elapsed = null;

            int CaptureElapsed()
            {
                elapsed ??= elapsedMilliseconds();
                return elapsed.Value;
            }

            if (metrics.FirstSseEventMs is null && !string.IsNullOrWhiteSpace(line))
            {
                metrics.FirstSseEventMs = CaptureElapsed();
            }

            if (metrics.TtftMs is null && countsForTtft(line))
            {
                metrics.TtftMs = CaptureElapsed();
            }

            if (metrics.FirstReasoningSummaryTextDeltaMs is null
                && line.Contains("response.reasoning_summary_text.delta", StringComparison.Ordinal))
            {
                metrics.FirstReasoningSummaryTextDeltaMs = CaptureElapsed();
            }

            if (metrics.FirstOutputTextDeltaMs is null
                && line.Contains("response.output_text.delta", StringComparison.Ordinal))
            {
                metrics.FirstOutputTextDeltaMs = CaptureElapsed();
            }

            if (metrics.FirstFunctionCallArgumentsDeltaMs is null
                && line.Contains("response.function_call_arguments.delta", StringComparison.Ordinal))
            {
                metrics.FirstFunctionCallArgumentsDeltaMs = CaptureElapsed();
            }

            if (metrics.CompletedEventMs is null
                && line.Contains("response.completed", StringComparison.Ordinal))
            {
                metrics.CompletedEventMs = CaptureElapsed();
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

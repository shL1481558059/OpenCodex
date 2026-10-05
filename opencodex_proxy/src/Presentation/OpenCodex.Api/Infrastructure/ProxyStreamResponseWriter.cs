using OpenCodex.CoreBase.Abstractions;
using System.Text;
using System.Text.Json;

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
        var flushedFrames = new FlushedSseFrames();
        try
        {
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
                flushedFrames.Append(line);
            }

            if (sawCompleted && !sawDone)
            {
                await response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && flushedFrames.SuccessfullyCompleted)
        {
            // A complete successful terminal frame was flushed before the peer closed.
        }

        return metrics;
    }

    private sealed class FlushedSseFrames
    {
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _data = new();
        public bool SuccessfullyCompleted { get; private set; }

        public void Append(string chunk)
        {
            foreach (var character in chunk)
            {
                if (character != '\n')
                {
                    _line.Append(character);
                    continue;
                }

                var line = _line.ToString().TrimEnd('\r');
                _line.Clear();
                if (line.Length == 0)
                {
                    InspectFrame();
                    _data.Clear();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (_data.Length > 0) _data.Append('\n');
                    _data.Append(line.AsSpan(5).TrimStart(' '));
                }
            }
        }

        private void InspectFrame()
        {
            if (SuccessfullyCompleted || _data.Length == 0) return;
            try
            {
                using var document = JsonDocument.Parse(_data.ToString());
                var root = document.RootElement;
                SuccessfullyCompleted = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String && type.GetString() == "response.completed"
                    && root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
                    && response.TryGetProperty("status", out var status)
                    && status.ValueKind == JsonValueKind.String && status.GetString() == "completed";
            }
            catch (JsonException) { }
        }
    }

}

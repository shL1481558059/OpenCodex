using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using System.Text;

namespace OpenCodex.Api.Infrastructure;

public sealed class SseWriteOptions
{
    public int MaxBatchBytes { get; set; } = 16 * 1024;
    public int MaxFlushDelayMilliseconds { get; set; } = 10;
    public int MaxEventBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxQueuedEvents { get; set; } = 8;
    public int MaxQueuedBytes { get; set; } = 1024 * 1024;
    internal bool AppendResponsesDone { get; set; } = true;

    internal void Validate()
    {
        if (MaxBatchBytes <= 0 || MaxFlushDelayMilliseconds <= 0 || MaxFlushDelayMilliseconds > 60_000 || MaxEventBytes <= 0 || MaxQueuedEvents <= 0 || MaxQueuedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(SseWriteOptions));
    }
}

public sealed class ProxyStreamResponseWriter : IProxyStreamWriter
{
    private readonly HttpResponse _response;
    private readonly SseWriteOptions _options;
    public ProxyStreamResponseWriter(HttpResponse response, SseWriteOptions? options = null)
    {
        _response = response;
        _options = options ?? response.HttpContext.RequestServices?.GetService<IOptions<SseWriteOptions>>()?.Value ?? new();
    }

    public void PrepareSse() => PrepareSse(_response);
    public Task<StreamWriteMetrics> WriteLinesAsync(IAsyncEnumerable<string> lines,
        Func<string, bool> countsForTtft, Func<int> elapsedMilliseconds, CancellationToken cancellationToken = default) =>
        WriteFramesAsync(SseFrameReader.ReadChunks(lines, cancellationToken,
            new() { MaxEventBytes = _options.MaxEventBytes }), countsForTtft, elapsedMilliseconds, cancellationToken);

    public Task<StreamWriteMetrics> WriteFramesAsync(IAsyncEnumerable<SseFrame> frames,
        Func<string, bool> countsForTtft, Func<int> elapsedMilliseconds, CancellationToken cancellationToken = default) =>
        WriteFramesAsync(_response, frames, countsForTtft, elapsedMilliseconds, cancellationToken, _options);

    public static void PrepareSse(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    // Legacy string consumers are normalized at the adapter. The event writer below never
    // parses JSON, searches payload strings, or assumes one string is one physical SSE line.
    public static Task<StreamWriteMetrics> WriteLinesAsync(HttpResponse response, IAsyncEnumerable<string> lines,
        Func<string, bool> countsForTtft, Func<int> elapsedMilliseconds, CancellationToken cancellationToken = default) =>
        WriteFramesAsync(response, SseFrameReader.ReadChunks(lines, cancellationToken),
            countsForTtft, elapsedMilliseconds, cancellationToken);

    public static async Task<StreamWriteMetrics> WriteFramesAsync(HttpResponse response,
        IAsyncEnumerable<SseFrame> frames, Func<string, bool> countsForTtft, Func<int> elapsedMilliseconds,
        CancellationToken cancellationToken = default, SseWriteOptions? options = null)
    {
        options ??= new();
        options.Validate();
        var metrics = new StreamWriteMetrics();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.MaxFlushDelayMilliseconds));
        var enumerator = frames.GetAsyncEnumerator(lifetime.Token);
        Task<bool>? move = null;
        Task<bool>? tick = null;
        var pendingBytes = 0;
        var pendingSuccess = false;
        var flushedSuccess = false;
        var first = true;
        var firstBusiness = true;
        var sawCompleted = false;
        var sawDone = false;
        var output = response.BodyWriter;
        try
        {
            move = enumerator.MoveNextAsync().AsTask();
            tick = timer.WaitForNextTickAsync(lifetime.Token).AsTask();
            while (true)
            {
                if (pendingBytes > 0 && (tick.IsCompleted || !move.IsCompleted))
                {
                    if (await Task.WhenAny(move, tick) == tick)
                    {
                        await tick;
                        await Flush();
                        tick = timer.WaitForNextTickAsync(lifetime.Token).AsTask();
                        continue;
                    }
                }
                lifetime.Token.ThrowIfCancellationRequested();
                if (!await move) break;
                var frame = enumerator.Current;
                var bytes = frame.Utf8Length;
                if (bytes > options.MaxEventBytes)
                    throw new InvalidDataException("SSE event exceeds configured writer MaxEventBytes; output was not truncated.");
                if (pendingBytes > 0 && (long)pendingBytes + bytes > options.MaxBatchBytes) await Flush();
                Append(frame, bytes);
                if (metrics.TtftMs is null && countsForTtft(frame.WireText)) metrics.TtftMs = elapsedMilliseconds();
                sawCompleted |= frame.IsResponseCompleted && frame.BoundaryComplete;
                sawDone |= frame.IsDone;
                if (first || firstBusiness && frame.Event is not null || frame.IsTerminal || frame.Event is null || pendingBytes >= options.MaxBatchBytes) await Flush();
                first = false;
                if (frame.Event is not null) firstBusiness = false;
                // Exactly one outstanding MoveNext. Awaited flush provides downstream backpressure;
                // while awaiting upstream we can flush pending complete events on the reusable timer.
                move = enumerator.MoveNextAsync().AsTask();
            }
            if (options.AppendResponsesDone && sawCompleted && !sawDone)
            {
                Append(SseFrame.Done, SseFrame.Done.Utf8Length);
            }
            await Flush();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && flushedSuccess)
        {
            // Only a complete successful terminal frame whose Flush actually returned is success.
        }
        finally
        {
            // A slow/downstream failure can leave one upstream read pending. Cancel then await it
            // before disposing; never race DisposeAsync against MoveNextAsync on an iterator.
            lifetime.Cancel();
            timer.Dispose();
            if (move is not null) { try { await move; } catch { } }
            if (tick is not null) { try { await tick; } catch { } }
            await enumerator.DisposeAsync();
        }
        return metrics;

        void Append(SseFrame frame, int bytes)
        {
            var memory = output.GetMemory(bytes);
            var written = Encoding.UTF8.GetBytes(frame.WireText, memory.Span);
            output.Advance(written);
            pendingBytes += written;
            pendingSuccess |= frame.IsSuccessfulResponseCompleted;
        }
        async Task Flush()
        {
            if (pendingBytes == 0) return;
            var result = await output.FlushAsync(lifetime.Token);
            if (result.IsCanceled) throw new OperationCanceledException(lifetime.Token);
            if (result.IsCompleted) throw new IOException("Downstream completed before SSE flush was accepted.");
            flushedSuccess |= pendingSuccess;
            pendingSuccess = false;
            pendingBytes = 0;
            lifetime.Token.ThrowIfCancellationRequested();
        }
    }
}

using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyStreamResponseWriterTests
{
    [Fact]
    public async Task WriteLinesAsync_RecordsGranularStreamMetrics()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var ticks = new Queue<int>(new[] { 10, 20, 30, 40 });
        var metrics = await ProxyStreamResponseWriter.WriteLinesAsync(
            context.Response,
            ToAsyncEnumerable(
                "event: response.created\ndata: {\"type\":\"response.created\"}\n\n",
                "event: response.reasoning_summary_text.delta\ndata: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"think\"}\n\n",
                "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}\n\n",
                "event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n"),
            SseStreamConverter.CountsForTtft,
            () => ticks.Dequeue(),
            CancellationToken.None);

        Assert.Equal(10, metrics.FirstSseEventMs);
        Assert.Equal(20, metrics.TtftMs);
        Assert.Equal(20, metrics.FirstReasoningSummaryTextDeltaMs);
        Assert.Equal(30, metrics.FirstOutputTextDeltaMs);
        Assert.Equal(40, metrics.CompletedEventMs);
    }

    [Fact]
    public async Task WriteLinesAsync_AppendsDoneAfterCompletedWhenMissing()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await ProxyStreamResponseWriter.WriteLinesAsync(
            context.Response,
            ToAsyncEnumerable(
                "event: response.created\ndata: {\"type\":\"response.created\"}\n\n",
                "event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n"),
            SseStreamConverter.CountsForTtft,
            () => 1,
            CancellationToken.None);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();

        Assert.Contains("event: response.completed", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteLinesAsync_DoesNotDuplicateDoneWhenAlreadyPresent()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await ProxyStreamResponseWriter.WriteLinesAsync(
            context.Response,
            ToAsyncEnumerable(
                "event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n",
                "data: [DONE]\n\n"),
            SseStreamConverter.CountsForTtft,
            () => 1,
            CancellationToken.None);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        var doneCount = body.Split("data: [DONE]\n\n", StringSplitOptions.None).Length - 1;

        Assert.Equal(1, doneCount);
    }

    [Fact]
    public async Task TrackingWriter_DeferPrepareSse_UntilFirstLineWritten()
    {
        var inner = new DeferredPrepareTrackingWriter();
        var tracking = new TrackingProxyStreamWriter(inner);

        Assert.False(tracking.IsPrepared);
        Assert.False(inner.PrepareSseCalled);

        await tracking.WriteLinesAsync(
            ToAsyncEnumerable("data: {\"type\":\"response.created\"}\n\n", "data: [DONE]\n\n"),
            static _ => true,
            static () => 1,
            CancellationToken.None);

        Assert.True(tracking.IsPrepared);
        Assert.True(inner.PrepareSseCalled);
        Assert.True(tracking.HasWritten);
    }

    [Fact]
    public async Task TrackingWriter_UpstreamThrowsBeforeFirstLine_NeverPreparesSse()
    {
        var inner = new DeferredPrepareTrackingWriter();
        var tracking = new TrackingProxyStreamWriter(inner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tracking.WriteLinesAsync(
                ThrowAfterYield(),
                static _ => true,
                static () => 1,
                CancellationToken.None));

        Assert.False(tracking.IsPrepared);
        Assert.False(inner.PrepareSseCalled);
        Assert.False(tracking.HasWritten);
    }

    [Fact]
    public async Task TrackingWriter_PrepareSseIsIdempotent()
    {
        var inner = new DeferredPrepareTrackingWriter();
        var tracking = new TrackingProxyStreamWriter(inner);

        tracking.PrepareSse();
        Assert.True(inner.PrepareSseCalled);
        Assert.Equal(1, inner.PrepareSseCallCount);

        tracking.PrepareSse();
        Assert.Equal(1, inner.PrepareSseCallCount);

        await tracking.WriteLinesAsync(
            ToAsyncEnumerable("data: {\"type\":\"response.created\"}\n\n"),
            static _ => true,
            static () => 1,
            CancellationToken.None);

        Assert.Equal(1, inner.PrepareSseCallCount);
        Assert.True(tracking.HasWritten);
    }

    [Fact]
    public async Task TrackingWriter_EmptyStream_NeverPreparesSse()
    {
        var inner = new DeferredPrepareTrackingWriter();
        var tracking = new TrackingProxyStreamWriter(inner);

        await tracking.WriteLinesAsync(
            ToAsyncEnumerable(),
            static _ => true,
            static () => 1,
            CancellationToken.None);

        Assert.False(tracking.IsPrepared);
        Assert.False(inner.PrepareSseCalled);
        Assert.False(tracking.HasWritten);
    }

    private static async IAsyncEnumerable<string> ToAsyncEnumerable(params string[] lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }

    private sealed class DeferredPrepareTrackingWriter : IProxyStreamWriter
    {
        public bool PrepareSseCalled { get; private set; }

        public int PrepareSseCallCount { get; private set; }

        public void PrepareSse()
        {
            PrepareSseCalled = true;
            PrepareSseCallCount++;
        }

        public async Task<StreamWriteMetrics> WriteLinesAsync(
            IAsyncEnumerable<string> lines,
            Func<string, bool> countsForTtft,
            Func<int> elapsedMilliseconds,
            CancellationToken cancellationToken = default)
        {
            var metrics = new StreamWriteMetrics();
            await foreach (var line in lines.WithCancellation(cancellationToken))
            {
                if (metrics.FirstSseEventMs is null && !string.IsNullOrWhiteSpace(line))
                {
                    metrics.FirstSseEventMs = elapsedMilliseconds();
                }

                if (metrics.TtftMs is null && countsForTtft(line))
                {
                    metrics.TtftMs = elapsedMilliseconds();
                }
            }

            return metrics;
        }
    }

    private static async IAsyncEnumerable<string> ThrowAfterYield()
    {
        await Task.Yield();
        throw new InvalidOperationException("upstream 503");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

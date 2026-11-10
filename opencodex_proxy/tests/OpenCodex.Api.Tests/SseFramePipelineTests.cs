using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class SseFramePipelineTests
{
    [Fact]
    public async Task TimerFlushesWhileUpstreamIdle_AndNeverRunsConcurrentMoveNext()
    {
        var channel = Channel.CreateBounded<SseFrame>(2);
        var source = new ObservedSource(channel.Reader);
        var body = new FlushStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        using var cancel = new CancellationTokenSource();
        var writing = ProxyStreamResponseWriter.WriteFramesAsync(response, source.Read(cancel.Token), _ => false,
            () => 1, cancel.Token, new() { MaxFlushDelayMilliseconds = 10 });
        await channel.Writer.WriteAsync(Frame(1));
        await body.First.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await channel.Writer.WriteAsync(Frame(2));
        await body.Second.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(writing.IsCompleted);
        Assert.Equal(1, source.MaxActive);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(source.Disposed);
        Assert.Equal(0, source.Active);
    }

    [Fact]
    public async Task BatchByteLimit_OneFirstFlushThenWholeEventBatches()
    {
        var frame = Frame(1);
        var body = new FlushStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        await ProxyStreamResponseWriter.WriteFramesAsync(response, Frames(Enumerable.Repeat(frame, 6)), _ => false,
            () => 1, options: new() { MaxBatchBytes = Encoding.UTF8.GetByteCount(frame.WireText) * 2, MaxFlushDelayMilliseconds = 60_000 });
        Assert.Equal(4, body.Count);
        Assert.Equal(string.Concat(Enumerable.Repeat(frame.WireText, 6)), Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task DownstreamFailure_CancelsAndAwaitsPendingReadBeforeDispose()
    {
        var channel = Channel.CreateBounded<SseFrame>(2);
        var source = new ObservedSource(channel.Reader);
        var body = new FlushStream { FailSecond = true };
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        var writing = ProxyStreamResponseWriter.WriteFramesAsync(response, source.Read(), _ => false, () => 1,
            options: new() { MaxFlushDelayMilliseconds = 10 });
        await channel.Writer.WriteAsync(Frame(1));
        await body.First.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await channel.Writer.WriteAsync(Frame(2));
        await Assert.ThrowsAsync<IOException>(() => writing.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(source.Disposed);
        Assert.Equal(0, source.Active);
        Assert.Equal(1, source.MaxActive);
    }

    [Fact]
    public async Task SlowDownstream_StopsReadingUntilFlushAccepted()
    {
        var channel = Channel.CreateBounded<SseFrame>(2);
        var source = new ObservedSource(channel.Reader);
        var body = new FlushStream { BlockFirst = true };
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        var writing = ProxyStreamResponseWriter.WriteFramesAsync(response, source.Read(), _ => false, () => 1);
        await channel.Writer.WriteAsync(Frame(1));
        await channel.Writer.WriteAsync(Frame(2));
        await body.First.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, source.Delivered);
        body.Release.TrySetResult();
        channel.Writer.Complete();
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, source.Delivered);
        Assert.True(source.Disposed);
    }

    [Theory]
    [InlineData("response.completed")]
    [InlineData("error")]
    [InlineData("message_stop")]
    [InlineData("[DONE]")]
    public async Task TerminalOrError_FlushesBufferedEventsImmediatelyWhileUpstreamRemainsOpen(string type)
    {
        var channel = Channel.CreateBounded<SseFrame>(3);
        var body = new FlushStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        var writing = ProxyStreamResponseWriter.WriteFramesAsync(response, new ObservedSource(channel.Reader).Read(),
            _ => false, () => 1, options: new() { MaxFlushDelayMilliseconds = 60_000 });
        await channel.Writer.WriteAsync(Frame(1));
        await body.First.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await channel.Writer.WriteAsync(Frame(2));
        var terminal = type == "[DONE]" ? SseFrame.Done : new SseFrameReader().Feed(
            "data: " + JsonSerializer.Serialize(new { type, response = new { status = "completed" } }) + "\n\n").Single();
        await channel.Writer.WriteAsync(terminal);
        await body.Second.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(writing.IsCompleted);
        channel.Writer.Complete();
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(terminal.WireText, Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeartbeatDoesNotDelayFirstBusinessFrame_AndFailedTerminalFlushNeverBecomesSuccess()
    {
        var channel = Channel.CreateBounded<SseFrame>(2);
        var body = new FlushStream { FailSecond = true };
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        var source = new ObservedSource(channel.Reader);
        var writing = ProxyStreamResponseWriter.WriteFramesAsync(response, source.Read(), _ => false, () => 1,
            options: new() { MaxFlushDelayMilliseconds = 60_000 });
        await channel.Writer.WriteAsync(new SseFrameReader().Feed(": ping\n\n").Single());
        await body.First.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await channel.Writer.WriteAsync(new SseFrameReader().Feed(
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n").Single());
        await Assert.ThrowsAsync<IOException>(() => writing.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(source.Disposed);
    }

    private static SseFrame Frame(int n) => new SseFrameReader().Feed($"data: {{\"n\":{n}}}\n\n").Single();
    private static async IAsyncEnumerable<SseFrame> Frames(IEnumerable<SseFrame> frames)
    {
        foreach (var frame in frames) { yield return frame; await Task.CompletedTask; }
    }
    private sealed class ObservedSource(ChannelReader<SseFrame> reader)
    {
        public int Active, MaxActive, Delivered;
        public bool Disposed;
        public async IAsyncEnumerable<SseFrame> Read([EnumeratorCancellation] CancellationToken token = default)
        {
            try
            {
                while (true)
                {
                    Active++;
                    MaxActive = Math.Max(MaxActive, Active);
                    SseFrame frame;
                    try
                    {
                        if (!await reader.WaitToReadAsync(token)) yield break;
                        frame = await reader.ReadAsync(token);
                    }
                    finally { Active--; }
                    Delivered++;
                    yield return frame;
                }
            }
            finally { Disposed = true; }
        }
    }
    private sealed class FlushStream : MemoryStream
    {
        public readonly TaskCompletionSource First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count;
        public bool FailSecond, BlockFirst;
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref Count);
            if (n == 1)
            {
                First.TrySetResult();
                if (BlockFirst) await Release.Task.WaitAsync(cancellationToken);
            }
            if (n == 2)
            {
                Second.TrySetResult();
                if (FailSecond) throw new IOException("injected downstream flush failure");
            }
        }
    }
}

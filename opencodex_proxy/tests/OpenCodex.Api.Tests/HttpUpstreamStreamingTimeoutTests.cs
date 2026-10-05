using System.Net;
using System.Text;
using OpenCodex.Core.Errors;
using OpenCodex.Core.ExternalIntegrations;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class HttpUpstreamStreamingTimeoutTests
{
    private const string Content = "data: {\"choices\":[{\"delta\":{\"content\":\"part\"}}]}\n\n";

    [Fact]
    public async Task BodyIdleTimeout_AfterPublishedContent_DoesNotRetryAndDisposesStream()
    {
        var stream = new ScheduledStream([(Content, 0)], hangAfter: true);
        var handler = new StreamHandler(stream);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = new List<string>();

        var error = await Assert.ThrowsAsync<UpstreamException>(async () =>
        {
            await foreach (var line in Client(handler).StreamJsonAsync(Channel(3), new Dictionary<string, object?>(), 1, stop.Token))
                received.Add(line);
        });

        Assert.Equal(504, error.StatusCode);
        Assert.Contains("idle", error.Message, StringComparison.Ordinal);
        Assert.Contains(received, line => line.Contains("part", StringComparison.Ordinal));
        Assert.Equal(1, handler.Calls);
        Assert.True(stream.Disposed);
        Assert.False(stop.IsCancellationRequested);
    }

    [Fact]
    public async Task ContinuousBody_CanOutliveFirstContentTimeout()
    {
        var stream = new ScheduledStream([(Content, 0), (Content, 400), (Content, 400), (Content, 400), ("data: [DONE]\n\n", 400)]);
        var handler = new StreamHandler(stream);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();

        await foreach (var line in Client(handler).StreamJsonAsync(Channel(0), new Dictionary<string, object?>(), 1, stop.Token))
            received.Add(line);

        Assert.Contains(received, line => line.Contains("[DONE]", StringComparison.Ordinal));
        Assert.Equal(1, handler.Calls);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ClientCancellation_DuringBody_RemainsCancellationAndDoesNotRetry()
    {
        var stream = new ScheduledStream([(Content, 0)], hangAfter: true);
        var handler = new StreamHandler(stream);
        using var stop = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var line in Client(handler).StreamJsonAsync(Channel(3), new Dictionary<string, object?>(), 1, stop.Token))
                if (line.Contains("part", StringComparison.Ordinal)) stop.Cancel();
        });

        Assert.Equal(1, handler.Calls);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task FirstContentTimeout_RetriesAreBoundedAndDisposeBeforeBackoff()
    {
        var first = new ScheduledStream([], hangAfter: true);
        var second = new ScheduledStream([], hangAfter: true);
        var handler = new StreamHandler(first, second);
        var delays = 0;
        var client = new HttpUpstreamClient(new HttpClient(handler), (_, _) =>
        {
            delays++;
            Assert.True(first.Disposed);
            return Task.CompletedTask;
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<UpstreamException>(async () =>
        {
            await foreach (var _ in client.StreamJsonAsync(Channel(1), new Dictionary<string, object?>(), 1, stop.Token)) { }
        });

        Assert.Equal(504, error.StatusCode);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, delays);
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task ConsumerStopsDuringBufferedPrefix_DisposesStream()
    {
        var stream = new ScheduledStream([(Content, 0)], hangAfter: true);
        var handler = new StreamHandler(stream);

        await foreach (var _ in Client(handler).StreamJsonAsync(Channel(0), new Dictionary<string, object?>(), 1, CancellationToken.None))
            break;

        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task TotalBudget_StopsContinuousBodyWithoutRetry()
    {
        var stream = new ScheduledStream([(Content, 0), (": heartbeat\n\n", 400), (": heartbeat\n\n", 400), (Content, 400)]);
        var handler = new StreamHandler(stream);
        var channel = Channel(3);
        channel["compat"] = new Dictionary<string, object?> { ["stream_total_timeout_seconds"] = 1 };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var error = await Assert.ThrowsAsync<UpstreamException>(async () =>
        {
            await foreach (var _ in Client(handler).StreamJsonAsync(channel, new Dictionary<string, object?>(), 1, stop.Token)) { }
        });

        Assert.Equal(504, error.StatusCode);
        Assert.Contains("total budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
        Assert.True(stream.Disposed);
        Assert.False(stop.IsCancellationRequested);
    }

    [Fact]
    public async Task TotalBudget_IncludesRetryBackoff()
    {
        var stream = new ScheduledStream([], hangAfter: true);
        var handler = new StreamHandler(stream);
        var channel = Channel(3);
        channel["compat"] = new Dictionary<string, object?> { ["stream_total_timeout_seconds"] = 2 };
        var delays = 0;
        var client = new HttpUpstreamClient(new HttpClient(handler), async (_, token) =>
        {
            delays++;
            Assert.True(stream.Disposed);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        var error = await Assert.ThrowsAsync<UpstreamException>(async () =>
        {
            await foreach (var _ in client.StreamJsonAsync(channel, new Dictionary<string, object?>(), 1, stop.Token)) { }
        });

        Assert.Equal(504, error.StatusCode);
        Assert.Contains("total budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, delays);
        Assert.True(stream.Disposed);
        Assert.False(stop.IsCancellationRequested);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData("invalid")]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(5000000)]
    [InlineData(null)]
    public async Task InvalidTotalBudget_IsRejectedBeforeRequest(object? budget)
    {
        var handler = new StreamHandler();
        var channel = Channel(0);
        channel["compat"] = new Dictionary<string, object?> { ["stream_total_timeout_seconds"] = budget };

        await Assert.ThrowsAsync<BadRequestException>(async () =>
        {
            await foreach (var _ in Client(handler).StreamJsonAsync(channel, new Dictionary<string, object?>(), 1, CancellationToken.None)) { }
        });
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(120, 3, 1290)]
    [InlineData(120, 0, 1200)]
    [InlineData(120, 10, 1620)]
    public void DefaultBudget_CoversLongOutputAndRetryWaits(int timeout, int retries, int totalSeconds)
    {
        var policy = StreamTimeoutPolicy.Create(Channel(retries), timeout, retries, 30);
        Assert.Equal(TimeSpan.FromSeconds(timeout), policy.FirstContent);
        Assert.Equal(TimeSpan.FromSeconds(timeout), policy.Idle);
        Assert.Equal(TimeSpan.FromSeconds(totalSeconds), policy.Total);
    }

    [Fact]
    public void LargeRetryBudget_IsFiniteAndSupportedByTimer()
    {
        var policy = StreamTimeoutPolicy.Create(Channel(int.MaxValue), int.MaxValue, int.MaxValue, 30);
        using var timer = new CancellationTokenSource();
        timer.CancelAfter(policy.Total);
        timer.CancelAfter(policy.FirstContent);
        Assert.Equal(TimeSpan.FromMilliseconds(uint.MaxValue - 1d), policy.Total);
    }

    private static HttpUpstreamClient Client(StreamHandler handler) => new(new HttpClient(handler), (_, _) => Task.CompletedTask);

    private static Dictionary<string, object?> Channel(int retries) => new()
    {
        ["id"] = "timeout-test", ["type"] = "chat", ["baseurl"] = "https://upstream.test/v1",
        ["api_key"] = "test", ["timeout_seconds"] = 1, ["retry_count"] = retries
    };

    private sealed class StreamHandler(params ScheduledStream[] streams) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(streams[Calls++]) });
    }

    private sealed class ScheduledStream((string Text, int DelayMs)[] chunks, bool hangAfter = false) : Stream
    {
        private int _index;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_index == chunks.Length)
            {
                if (hangAfter) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            var chunk = chunks[_index++];
            if (chunk.DelayMs > 0) await Task.Delay(chunk.DelayMs, cancellationToken);
            return Encoding.UTF8.GetBytes(chunk.Text, buffer.Span);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

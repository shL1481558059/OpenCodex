using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenCodex.Api.Infrastructure;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class RequestBodyReaderTests
{
    [Fact]
    public async Task ReadJsonPayload_ReusesOwnedBodyMemoryWithoutAnAuditSpoolSlot()
    {
        var body = Encoding.UTF8.GetBytes("""{"model":"test","input":"hello"}""");
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;

        await using var payload = await new RequestBodyReader().ReadJsonPayloadAsync(context.Request);

        var handle = Assert.IsType<ProxyLogBodyHandle>(RequestBodyReader.ReadCapturedRawBody(context.Request));
        var snapshot = handle.ReadSnapshot();
        Assert.Null(snapshot.Spool);
        Assert.Equal(body, snapshot.Utf8!.Value.ToArray());
        handle.Dispose();
    }

    [Fact]
    public async Task ReadJsonPayload_RejectsKnownOversizeBeforeReadingTheBody()
    {
        var body = new TrackingStream(16);
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        context.Request.ContentLength = 1024 * 1024 + 1;
        context.RequestServices = Services();
        var reader = new RequestBodyReader();

        var error = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => reader.ReadJsonPayloadAsync(context.Request));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, error.StatusCode);
        Assert.Equal(0, body.ReadCalls);
    }

    [Fact]
    public async Task ReadJsonPayload_StopsChunkedOversizeAtTheConfiguredLimit()
    {
        var body = new TrackingStream(2 * 1024 * 1024);
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        context.RequestServices = Services();
        var reader = new RequestBodyReader();

        var error = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => reader.ReadJsonPayloadAsync(context.Request));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, error.StatusCode);
        Assert.InRange(body.BytesRead, 1024 * 1024 + 1, 1024 * 1024 + 16 * 1024);
        Assert.Null(RequestBodyReader.ReadCapturedRawBody(context.Request));
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenCodex:MaxRequestBodyMb"] = "1"
            })
            .Build();

    private static IServiceProvider Services() =>
        new ServiceCollection()
            .AddSingleton(Configuration())
            .BuildServiceProvider();

    private sealed class TrackingStream(int length) : Stream
    {
        private int _position;
        private readonly byte[] _buffer = new byte[length];

        public int ReadCalls { get; private set; }

        public int BytesRead => _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            var written = Math.Min(count, _buffer.Length - _position);
            if (written <= 0) return 0;
            Array.Copy(_buffer, _position, buffer, offset, written);
            _position += written;
            return written;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            var written = Math.Min(buffer.Length, _buffer.Length - _position);
            if (written <= 0) return ValueTask.FromResult(0);
            _buffer.AsSpan(_position, written).CopyTo(buffer.Span);
            _position += written;
            return ValueTask.FromResult(written);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _buffer.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(Span<byte> buffer) => Read(buffer.ToArray(), 0, buffer.Length);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

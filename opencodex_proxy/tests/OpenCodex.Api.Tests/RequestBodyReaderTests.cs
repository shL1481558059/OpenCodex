using System.Text;
using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Infrastructure;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.ProtocolConversion;
using Xunit;

namespace OpenCodex.Api.Tests;

[Collection("Root request capacity")]
public sealed class RequestBodyReaderTests
{
    [Fact]
    public async Task ReadJsonPayload_ReusesOwnedBodyMemoryWithoutAnAuditSpoolSlot()
    {
        var body = Encoding.UTF8.GetBytes("""{"model":"test","input":"hello"}""");
        using var source = new MemoryStream(body);
        var context = new DefaultHttpContext();
        context.Request.Body = source;
        context.Request.ContentLength = body.Length;
        await using var payload = await new RequestBodyReader().ReadJsonPayloadAsync(context.Request);
        Assert.NotNull(payload);
        var handle = Assert.IsType<ProxyLogBodyHandle>(RequestBodyReader.ReadCapturedRawBody(context.Request));
        try
        {
            var snapshot = handle.ReadSnapshot();
            Assert.Null(snapshot.Spool);
            Assert.Equal(body, snapshot.Utf8!.Value.ToArray());
            var materialized = await payload.MaterializeObjectAndReleaseAsync();
            Assert.NotNull(materialized);
            Assert.Equal("test", JsonDictionaryValue.String(materialized, "model"));
            Assert.Equal("hello", JsonDictionaryValue.String(materialized, "input"));
        }
        finally { handle.Dispose(); }
    }

    [Fact]
    public Task ReadJsonPayload_AcceptsRootBoundaryAndPreservesRawContent() =>
        RootRequestCapacityTestProcess.RunAsync(typeof(RequestBodyReaderTests), nameof(ReadJsonPayload_AcceptsRootBoundaryAndPreservesRawContent),
            async (budget, cancellationToken) =>
            {
                foreach (var size in new[] { checked(budget - 1), budget })
                foreach (var knownLength in new[] { false, true })
                {
                    var bytes = RootRequestCapacityTestProcess.JsonBodyAtBytes(size, "test");
                    using var body = new TrackingStream(bytes);
                    var context = new DefaultHttpContext();
                    context.Request.Body = body;
                    context.Request.ContentLength = knownLength ? (long?)size : null;
                    await using var payload = await new RequestBodyReader().ReadJsonPayloadAsync(context.Request, cancellationToken);
                    Assert.NotNull(payload);
                    var handle = Assert.IsType<ProxyLogBodyHandle>(RequestBodyReader.ReadCapturedRawBody(context.Request));
                    try
                    {
                        var snapshot = handle.ReadSnapshot();
                        Assert.Null(snapshot.Spool);
                        Assert.Equal(bytes, snapshot.Utf8!.Value.ToArray());
                        Assert.Equal(size, body.BytesRead);
                        var materialized = await payload.MaterializeObjectAndReleaseAsync(cancellationToken);
                        Assert.NotNull(materialized);
                        Assert.Equal("test", JsonDictionaryValue.String(materialized, "model"));
                        Assert.Equal("hello", JsonDictionaryValue.String(materialized, "input"));
                    }
                    finally { handle.Dispose(); }
                }
            });

    [Fact]
    public Task ReadJsonPayload_RejectsKnownOversizeBeforeReadingTheBody() =>
        RootRequestCapacityTestProcess.RunAsync(typeof(RequestBodyReaderTests), nameof(ReadJsonPayload_RejectsKnownOversizeBeforeReadingTheBody),
            async (budget, cancellationToken) =>
            {
                using var body = new TrackingStream(Encoding.UTF8.GetBytes("""{"model":"test","input":"hello"}"""));
                var context = new DefaultHttpContext();
                context.Request.Body = body;
                context.Request.ContentLength = checked((long)budget + 1);
                var error = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
                    new RequestBodyReader().ReadJsonPayloadAsync(context.Request, cancellationToken));
                Assert.Equal(StatusCodes.Status413PayloadTooLarge, error.StatusCode);
                Assert.Equal(0, body.ReadCalls);
                Assert.Equal(0, body.BytesRead);
                Assert.Null(RequestBodyReader.ReadCapturedRawBody(context.Request));
            });

    [Fact]
    public Task ReadJsonPayload_StopsChunkedOversizeAtTheConfiguredLimit() =>
        RootRequestCapacityTestProcess.RunAsync(typeof(RequestBodyReaderTests), nameof(ReadJsonPayload_StopsChunkedOversizeAtTheConfiguredLimit),
            async (budget, cancellationToken) =>
            {
                using var body = new TrackingStream(RootRequestCapacityTestProcess.JsonBodyAtBytes(checked(2 * budget), "test"));
                var context = new DefaultHttpContext();
                context.Request.Body = body;
                context.Request.ContentLength = null;
                var error = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
                    new RequestBodyReader().ReadJsonPayloadAsync(context.Request, cancellationToken));
                Assert.Equal(StatusCodes.Status413PayloadTooLarge, error.StatusCode);
                Assert.InRange(body.BytesRead, budget + 1, checked(budget + ProtocolConversionParameters.Current.FlushBytes));
                Assert.Null(RequestBodyReader.ReadCapturedRawBody(context.Request));
            });

    private sealed class TrackingStream(byte[] bytes) : Stream
    {
        private int _position;
        public int ReadCalls { get; private set; }
        public int BytesRead => _position;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            var written = Math.Min(buffer.Length, bytes.Length - _position);
            if (written <= 0) return 0;
            bytes.AsSpan(_position, written).CopyTo(buffer);
            _position += written;
            return written;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

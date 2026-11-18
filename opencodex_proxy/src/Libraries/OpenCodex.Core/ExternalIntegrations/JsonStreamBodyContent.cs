using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Core.ExternalIntegrations;

internal sealed class JsonStreamBodyContent(object? value) : HttpContent
{
    public JsonStreamBodyContent(object? value, string mediaType) : this(value)
    {
        Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var writer = new Utf8JsonWriter(stream);
        ProxyAuditSpool.WriteJson(writer, value, 0);
        writer.Flush();
        return Task.CompletedTask;
    }

    protected override bool TryComputeLength(out long length)
    {
        using var counter = new CountingWriteStream();
        using (var writer = new Utf8JsonWriter(counter))
        {
            ProxyAuditSpool.WriteJson(writer, value, 0);
            writer.Flush();
        }
        length = counter.Length;
        return true;
    }

    private sealed class CountingWriteStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

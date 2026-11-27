namespace OpenCodex.ProtocolConversion.Abstractions;

internal static class AuditBufferStreamExtensions
{
    internal static Stream AsStream(this IProtocolAuditBuffer buffer) =>
        new AuditBufferStream(buffer);

    private sealed class AuditBufferStream(IProtocolAuditBuffer buffer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => buffer.Length;
        public override long Position
        {
            get => buffer.Length;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer1, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer1, int offset, int count) =>
            buffer.Write(buffer1.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> source) => buffer.Write(source);
        public override void WriteByte(byte value)
        {
            Span<byte> one = stackalloc byte[1];
            one[0] = value;
            buffer.Write(one);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                buffer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

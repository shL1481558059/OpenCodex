using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.ProtocolConversion.Json;

namespace OpenCodex.ProtocolConversion.Streaming;

internal static class GeneratedSseSerialization
{
    private static readonly ProtocolConversionParameters Parameters = ProtocolConversionParameters.Current;

    internal static bool IsSmall(object? data, string? name)
    {
        long remaining = Parameters.GeneratedFrameCacheBytes - 32 - 6L * (name?.Length ?? 0);
        return Visit(data, ref remaining);
    }

    private static bool Visit(object? value, ref long remaining)
    {
        remaining -= 32;
        if (remaining < 0)
        {
            return false;
        }

        switch (value)
        {
            case string text:
                remaining -= 6L * text.Length;
                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                foreach (var pair in dictionary)
                {
                    remaining -= 6L * pair.Key.Length + 4;
                    if (remaining < 0 || !Visit(pair.Value, ref remaining))
                        return false;
                }
                break;
            case IReadOnlyList<object?> items:
                foreach (var item in items)
                    if (!Visit(item, ref remaining))
                        return false;
                break;
            case null or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal:
                break;
            default:
                return false;
        }
        return remaining >= 0;
    }

    internal static byte[] Encode(object? data, string? name)
    {
        using var output = new PooledBufferWriter();
        if (name is not null)
        {
            output.Write("event: "u8);
            WriteText(output, name);
            output.Write("\n"u8);
        }
        output.Write("data: "u8);
        using (var writer = new Utf8JsonWriter(output, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            ProtocolJsonWriter.Write(writer, data, 0);
        output.Write("\n\n"u8);
        return output.WrittenSpan.ToArray();
    }

    private sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(Parameters.GeneratedFrameCacheBytes);
        private int _written;
        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
        public void Advance(int count) => _written = checked(_written + count);
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            sizeHint = Math.Max(1, sizeHint);
            if (_buffer.Length - _written < sizeHint)
            {
                var next = ArrayPool<byte>.Shared.Rent(checked(_written + sizeHint));
                WrittenSpan.CopyTo(next);
                ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
                _buffer = next;
            }
            return _buffer.AsMemory(_written);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
    }

    internal static int Count(object? data, string? name)
    {
        using var sink = new CountingStream();
        Write(sink, data, name);
        return sink.Count;
    }

    internal static string ToWire(object? data, string? name)
    {
        using var sink = new TextStream();
        Write(sink, data, name);
        return sink.Finish();
    }

    internal static string DiagnosticPrefix(object? data, int maxCharacters)
    {
        if (maxCharacters <= 0)
        {
            return "";
        }

        using var sink = new TextStream(maxCharacters);
        try
        {
            sink.Write("data: "u8);
            using var writer = new Utf8JsonWriter(sink, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            ProtocolJsonWriter.Write(writer, data, 0);
        }
        catch (PrefixCapturedException)
        {
        }
        return sink.Prefix();
    }

    private sealed class PrefixCapturedException : Exception;

    private static void Write(Stream sink, object? data, string? name)
    {
        if (name is not null)
        {
            sink.Write("event: "u8);
            WriteText(sink, name);
            sink.Write("\n"u8);
        }

        sink.Write("data: "u8);
        using (var writer = new Utf8JsonWriter(sink, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            ProtocolJsonWriter.Write(writer, data, 0);
        }
        sink.Write("\n\n"u8);
    }

    private static void WriteText(Stream sink, string text)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(Parameters.StringSegmentCharacters));
        try
        {
            for (var offset = 0; offset < text.Length;)
            {
                var size = Math.Min(Parameters.StringSegmentCharacters, text.Length - offset);
                if (offset + size < text.Length && char.IsHighSurrogate(text[offset + size - 1]))
                {
                    size--;
                }
                var count = Encoding.UTF8.GetBytes(text.AsSpan(offset, size), bytes);
                sink.Write(bytes.AsSpan(0, count));
                offset += size;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }

    private abstract class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public abstract override void Write(ReadOnlySpan<byte> buffer);
    }

    internal static int WriteUtf8(IBufferWriter<byte> output, object? data, string? name)
    {
        using var sink = new BufferStream(output);
        Write(sink, data, name);
        return sink.Count;
    }

    internal static int WriteUtf8(IBufferWriter<byte> output, string wire)
    {
        return WriteText(output, wire);
    }

    private static int WriteText(IBufferWriter<byte> output, string text)
    {
        var total = 0;
        for (var offset = 0; offset < text.Length;)
        {
            var count = Math.Min(Parameters.StringSegmentCharacters, text.Length - offset);
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]))
                count--;
            var segment = text.AsSpan(offset, count);
            var written = Encoding.UTF8.GetBytes(segment, output.GetSpan(Encoding.UTF8.GetByteCount(segment)));
            output.Advance(written);
            total = checked(total + written);
            offset += count;
        }
        return total;
    }

    internal static int WriteUtf8(IBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        var total = bytes.Length;
        while (!bytes.IsEmpty)
        {
            var count = Math.Min(bytes.Length, Parameters.FlushBytes);
            bytes[..count].CopyTo(output.GetSpan(count));
            output.Advance(count);
            bytes = bytes[count..];
        }
        return total;
    }

    private sealed class BufferStream(IBufferWriter<byte> output) : WriteOnlyStream
    {
        internal int Count;

        public override void Write(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                var count = Math.Min(bytes.Length, Parameters.FlushBytes);
                bytes[..count].CopyTo(output.GetSpan(count));
                output.Advance(count);
                Count = checked(Count + count);
                bytes = bytes[count..];
            }
        }
    }

    private sealed class CountingStream : WriteOnlyStream
    {
        internal int Count { get; private set; }

        public override void Write(ReadOnlySpan<byte> buffer) => Count = checked(Count + buffer.Length);
    }

    private sealed class TextStream(int maxCharacters = int.MaxValue) : WriteOnlyStream
    {
        private readonly StringBuilder _text = new();
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly char[] _chars = ArrayPool<char>.Shared.Rent(Parameters.StringSegmentCharacters);

        public override void Write(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                _decoder.Convert(bytes, _chars, false, out var consumed, out var count, out _);
                var accepted = Math.Min(count, maxCharacters - _text.Length);
                _text.Append(_chars.AsSpan(0, accepted));
                if (_text.Length >= maxCharacters)
                {
                    throw new PrefixCapturedException();
                }
                bytes = bytes[consumed..];
            }
        }

        internal string Prefix() => _text.ToString();

        internal string Finish()
        {
            var count = _decoder.GetChars(ReadOnlySpan<byte>.Empty, _chars, true);
            _text.Append(_chars.AsSpan(0, count));
            return _text.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ArrayPool<char>.Shared.Return(_chars, clearArray: true);
            }
            base.Dispose(disposing);
        }
    }
}

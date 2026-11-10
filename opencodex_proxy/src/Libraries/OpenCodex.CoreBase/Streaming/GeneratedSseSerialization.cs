using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Core.Protocols;

// Counting a terminal event must not retain its full UTF16 wire before auditing. Legacy
// string APIs still need one final string, but not a full intermediate JSON string or
// serializer's whole-token byte/char buffers. Reuse the audit bounded string encoder.
internal static class GeneratedSseSerialization
{
    // Retain the existing single-encode fast path only when a conservative JSON byte
    // upper bound fits 32KiB. Long terminals never take this whole-token serializer path.
    internal static bool IsSmall(object? data, string? name)
    {
        long remaining = 32 * 1024 - 32 - 6L * (name?.Length ?? 0);
        return Visit(data);
        bool Visit(object? value)
        {
            remaining -= 32;
            if (remaining < 0) return false;
            switch (value)
            {
                case string text: remaining -= 6L * text.Length; break;
                case IReadOnlyDictionary<string,object?> dictionary:
                    foreach (var pair in dictionary)
                    {
                        remaining -= 6L * pair.Key.Length + 4;
                        if (remaining < 0 || !Visit(pair.Value)) return false;
                    }
                    break;
                case IReadOnlyList<object?> items:
                    foreach (var item in items) if (!Visit(item)) return false;
                    break;
            }
            return remaining >= 0;
        }
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
        if (maxCharacters <= 0) return "";
        using var sink = new TextStream(maxCharacters);
        try
        {
            sink.Write("data: "u8);
            using var writer = new Utf8JsonWriter(sink, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            ProxyAuditSpool.WriteJson(writer, data, 0);
        }
        catch (PrefixCapturedException) { /* Only the explicitly limited diagnostic is clipped; audit is untouched. */ }
        return sink.Prefix();
    }
    private sealed class PrefixCapturedException : Exception;
    private static void Write(Stream sink, object? data, string? name)
    {
        if (name is not null)
        {
            sink.Write("event: "u8);
            // Event names are normally short; segment even untrusted/extension names.
            WriteText(sink, name);
            sink.Write("\n"u8);
        }
        sink.Write("data: "u8);
        using (var writer = new Utf8JsonWriter(sink, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            ProxyAuditSpool.WriteJson(writer, data, 0);
        sink.Write("\n\n"u8);
    }
    private static void WriteText(Stream sink, string text)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            for (var offset = 0; offset < text.Length;)
            {
                var size = Math.Min(4096, text.Length - offset);
                if (offset + size < text.Length && char.IsHighSurrogate(text[offset + size - 1])) size--;
                var count = Encoding.UTF8.GetBytes(text.AsSpan(offset, size), bytes);
                sink.Write(bytes.AsSpan(0, count));
                offset += size;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
    }
    private abstract class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public abstract override void Write(ReadOnlySpan<byte> buffer);
    }
    internal static int WriteUtf8(IBufferWriter<byte> output,object? data,string? name)
    {
        using var sink=new BufferStream(output);Write(sink,data,name);return sink.Count;
    }
    internal static int WriteUtf8(IBufferWriter<byte> output,string wire)
    {
        using var sink=new BufferStream(output);WriteText(sink,wire);return sink.Count;
    }
    private sealed class BufferStream(IBufferWriter<byte> output):WriteOnlyStream
    {
        internal int Count;
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            while(!bytes.IsEmpty)
            {
                var count=Math.Min(bytes.Length,16*1024);bytes[..count].CopyTo(output.GetSpan(count));
                output.Advance(count);Count=checked(Count+count);bytes=bytes[count..];
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
        private readonly char[] _chars = ArrayPool<char>.Shared.Rent(4096);
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                _decoder.Convert(bytes, _chars, false, out var consumed, out var count, out _);
                var accepted = Math.Min(count, maxCharacters - _text.Length);
                _text.Append(_chars.AsSpan(0, accepted));
                if (_text.Length >= maxCharacters) throw new PrefixCapturedException();
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
            if (disposing) ArrayPool<char>.Shared.Return(_chars, clearArray: true);
            base.Dispose(disposing);
        }
    }
}

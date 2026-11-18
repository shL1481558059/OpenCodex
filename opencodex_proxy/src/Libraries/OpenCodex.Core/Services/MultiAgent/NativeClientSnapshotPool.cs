using System.Security.Cryptography;
using System.Text.Json;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>Computes stable content identities without materializing canonical JSON text.</summary>
internal static class NativeClientSnapshotPool
{
    internal static string Hash(object? value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var writer = new Utf8JsonWriter(new HashWriteStream(hash)))
        {
            WriteCanonical(writer, value, 0);
            writer.Flush();
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, object? value, int depth)
    {
        if (depth > 64) throw new JsonException("JSON depth limit exceeded.");
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> dictionary:
                writer.WriteStartObject();
                foreach (var pair in dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    WriteCanonical(writer, pair.Value, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case IReadOnlyList<object?> list:
                writer.WriteStartArray();
                foreach (var item in list) WriteCanonical(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            case JsonElement element:
                WriteCanonical(writer, element, depth + 1);
                break;
            default:
                JsonSerializer.Serialize(writer, value);
                break;
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, int depth)
    {
        if (depth > 64) throw new JsonException("JSON depth limit exceeded.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value, depth + 1);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var child in element.EnumerateArray()) WriteCanonical(writer, child, depth + 1);
            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }

    private sealed class HashWriteStream(IncrementalHash hash) : Stream
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

        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

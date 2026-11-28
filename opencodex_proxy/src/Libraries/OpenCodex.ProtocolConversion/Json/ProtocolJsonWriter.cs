using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCodex.ProtocolConversion.Abstractions;
using OpenCodex.ProtocolConversion.Protocols;

namespace OpenCodex.ProtocolConversion.Json;

internal static class ProtocolJsonWriter
{
    private static readonly ProtocolConversionParameters Parameters = ProtocolConversionParameters.Current;
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new SegmentedStringConverter() }
    };

    internal static long Count(object? value, bool audit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sink = new CountingStream(cancellationToken);
        using (var writer = new Utf8JsonWriter(sink, new()
        {
            Encoder = audit ? JavaScriptEncoder.UnsafeRelaxedJsonEscaping : JavaScriptEncoder.Default
        }))
        {
            if (audit || value is IProtocolJsonValue) Write(writer, value, 0, cancellationToken);
            else JsonSerializer.Serialize(writer, value);
        }
        return sink.Length;
    }

    // Uses the same segmented writer as HTTP/audit content, with the encoder
    // chosen explicitly. Existing Count keeps its response-capture semantics.
    internal static long Measure(object? value, JavaScriptEncoder encoder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        cancellationToken.ThrowIfCancellationRequested();
        using var sink = new CountingStream(cancellationToken);
        using (var writer = new Utf8JsonWriter(sink, new() { Encoder = encoder }))
        {
            Write(writer, value, 0, cancellationToken);
        }
        return sink.Length;
    }

    // A root-only request budget. No caller-selected request limit can bypass it.
    // Type override/exclusions mirror the final Responses WS root on the wire.
    internal static long ValidateRequest(
        object? value,
        CancellationToken cancellationToken,
        string? typeOverride = null,
        IReadOnlySet<string>? excludedProperties = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sink = new CountingStream(cancellationToken, Parameters.MaxRequestBodyBytes);
        using (var writer = new Utf8JsonWriter(sink))
        {
            if (typeOverride is null && excludedProperties is null)
            {
                Write(writer, value, 0, cancellationToken);
            }
            else
            {
                if (value is not IReadOnlyDictionary<string, object?> dictionary)
                    throw new ArgumentException("A JSON object is required for a request root override.", nameof(value));
                writer.WriteStartObject();
                if (typeOverride is not null) writer.WriteString("type", typeOverride);
                foreach (var pair in dictionary)
                {
                    if (pair.Key == "type" && typeOverride is not null
                        || excludedProperties?.Contains(pair.Key) == true) continue;
                    writer.WritePropertyName(pair.Key);
                    Write(writer, pair.Value, 1, cancellationToken);
                }
                writer.WriteEndObject();
            }
        }
        return sink.Length;
    }

    private sealed class CountingStream(CancellationToken cancellationToken, long? maximumBytes = null) : Stream
    {
        private long _length;
        private RequestBudgetExceededException? _budgetExceeded;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_budgetExceeded is not null) throw _budgetExceeded;
            _length = checked(_length + buffer.Length);
            if (maximumBytes is { } maximum && _length > maximum)
                throw _budgetExceeded = new RequestBudgetExceededException(maximum, _length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public static void Write(Utf8JsonWriter writer, object? value, int depth, CancellationToken cancellationToken = default)
    {
        if (depth > 64)
        {
            throw new JsonException("Protocol JSON exceeds maximum depth or contains a cycle.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (value is IProtocolJsonValue streamed)
        {
            streamed.Write(writer, cancellationToken);
        }
        else if (value is IReadOnlyDictionary<string, object?> dictionary)
        {
            writer.WriteStartObject();
            foreach (var pair in dictionary)
            {
                writer.WritePropertyName(pair.Key);
                Write(writer, pair.Value, depth + 1, cancellationToken);
            }
            writer.WriteEndObject();
        }
        else if (value is IEnumerable<object?> items)
        {
            writer.WriteStartArray();
            foreach (var item in items)
            {
                Write(writer, item, depth + 1, cancellationToken);
            }
            writer.WriteEndArray();
        }
        else
        {
            switch (value)
            {
                case null: writer.WriteNullValue(); break;
                case string text: WriteString(writer, text); break;
                case bool boolean: writer.WriteBooleanValue(boolean); break;
                case byte number: writer.WriteNumberValue(number); break;
                case sbyte number: writer.WriteNumberValue(number); break;
                case short number: writer.WriteNumberValue(number); break;
                case ushort number: writer.WriteNumberValue(number); break;
                case int number: writer.WriteNumberValue(number); break;
                case uint number: writer.WriteNumberValue(number); break;
                case long number: writer.WriteNumberValue(number); break;
                case ulong number: writer.WriteNumberValue(number); break;
                case float number: writer.WriteNumberValue(number); break;
                case double number: writer.WriteNumberValue(number); break;
                case decimal number: writer.WriteNumberValue(number); break;
                default: JsonSerializer.Serialize(writer, value, Options); break;
            }
        }
        FlushBatch(writer);
    }

    private static void FlushBatch(Utf8JsonWriter writer)
    {
        if (writer.BytesPending >= Parameters.FlushBytes)
        {
            writer.Flush();
        }
    }

    private static void WriteString(Utf8JsonWriter writer, string value)
    {
        if (value.Length == 0)
        {
            writer.WriteStringValue(value);
            return;
        }

        for (var offset = 0; offset < value.Length;)
        {
            var count = Math.Min(Parameters.StringSegmentCharacters, value.Length - offset);
            if (offset + count < value.Length && char.IsHighSurrogate(value[offset + count - 1]))
            {
                count--;
            }
            writer.WriteStringValueSegment(value.AsSpan(offset, count), offset + count == value.Length);
            FlushBatch(writer);
            offset += count;
        }
    }

    private sealed class SegmentedStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WritePropertyName(value);

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            WriteString(writer, value);
    }

    public static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => FromJsonElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => NumberValue(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static object NumberValue(JsonElement element)
    {
        if (!element.TryGetInt64(out var longValue))
        {
            return element.GetDouble();
        }
        return longValue is >= int.MinValue and <= int.MaxValue ? (int)longValue : longValue;
    }
}

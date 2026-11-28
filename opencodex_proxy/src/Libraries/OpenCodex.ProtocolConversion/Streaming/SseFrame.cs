using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenCodex.ProtocolConversion.Streaming;

public sealed class SseEvent(string eventName, object? data)
{
    public string EventName { get; } = eventName;
    public object? Data { get; } = data;
}

/// <summary>
/// An accepted JSON message and its exclusively owned UTF-8 bytes. The receiver transfers a
/// fresh array after EOM and object parsing; no view of its reusable network buffer is retained.
/// Reads cannot expose or mutate that array, and evidence never reserializes Payload.
/// </summary>
internal sealed class ReceivedJsonMessage
{
    internal const string WireFormat = "responses_websocket_text_json_utf8";
    private readonly byte[] _ownedUtf8;

    internal ReceivedJsonMessage(Dictionary<string, object?> payload, byte[] ownedUtf8)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(ownedUtf8);
        Payload = payload;
        _ownedUtf8 = ownedUtf8;
    }

    internal Dictionary<string, object?> Payload { get; }
    internal int Utf8Length => _ownedUtf8.Length;
    internal Stream OpenRead() => new MemoryStream(_ownedUtf8, 0, _ownedUtf8.Length,
        writable: false, publiclyVisible: false);
    internal int WriteUtf8(IBufferWriter<byte> output) =>
        GeneratedSseSerialization.WriteUtf8(output, _ownedUtf8.AsSpan());
}

public sealed class SseFrame
{
    public SseFrame(string wireText, SseEvent? decoded, bool boundaryComplete = true, bool malformed = false)
    {
        _wireText = wireText;
        Event = decoded;
        BoundaryComplete = boundaryComplete;
        Malformed = malformed;
        var payload = decoded?.Data as IReadOnlyDictionary<string, object?>;
        Type = payload?.GetValueOrDefault("type") as string ?? decoded?.EventName ?? "";
        IsDone = decoded?.Data is string text && text == "[DONE]";
        IsResponseCompleted = Type == "response.completed";
        IsSuccessfulResponseCompleted = boundaryComplete && IsResponseCompleted
            && payload?.GetValueOrDefault("response") is IReadOnlyDictionary<string, object?> response
            && response.GetValueOrDefault("status") is "completed";
        IsError = Type is "error" or "response.failed" or "response.incomplete"
            || payload?.GetValueOrDefault("error") is not null;
        IsTerminal = IsDone || IsResponseCompleted || IsError || Type == "message_stop";
    }

    private readonly Lazy<string>? _generatedWire;
    private readonly Lazy<byte[]>? _generatedUtf8;
    private readonly string? _generatedEventName;
    private readonly ReceivedJsonMessage? _receivedMessage;
    private readonly Lazy<string>? _receivedWire;
    private readonly string _wireText;
    public string WireText => _receivedWire?.Value ?? _generatedWire?.Value ?? _wireText;
    private int _utf8Length = -1;

    public int Utf8Length
    {
        get
        {
            var cached = Volatile.Read(ref _utf8Length);
            if (cached >= 0)
            {
                return cached;
            }

            var length = _receivedWire is not null
                ? System.Text.Encoding.UTF8.GetByteCount(WireText)
                : _generatedUtf8 is not null
                    ? _generatedUtf8.Value.Length
                    : _generatedWire is not null && !_generatedWire.IsValueCreated
                        ? GeneratedSseSerialization.Count(Event!.Data, _generatedEventName)
                        : System.Text.Encoding.UTF8.GetByteCount(WireText);
            Interlocked.CompareExchange(ref _utf8Length, length, -1);
            return length;
        }
    }

    public int WriteUtf8(System.Buffers.IBufferWriter<byte> output) =>
        _receivedWire is not null
            ? GeneratedSseSerialization.WriteUtf8(output, WireText)
            : _generatedUtf8 is not null
                ? GeneratedSseSerialization.WriteUtf8(output, _generatedUtf8.Value.AsSpan())
                : _generatedWire is not null && !_generatedWire.IsValueCreated
                    ? GeneratedSseSerialization.WriteUtf8(output, Event!.Data, _generatedEventName)
                    : GeneratedSseSerialization.WriteUtf8(output, WireText);

    /// <summary>The original accepted WS message, not its semantic SSE adapter; null for source SSE.</summary>
    public int? OriginalMessageUtf8Length => _receivedMessage?.Utf8Length;

    /// <summary>Independent immutable original-message read; no writable or publicly visible backing array.</summary>
    public Stream? OpenOriginalMessageRead() => _receivedMessage?.OpenRead();

    public string EvidenceWireFormat => _receivedMessage is null ? "sse_frame_utf8" : ReceivedJsonMessage.WireFormat;
    internal int EvidenceUtf8Length => _receivedMessage?.Utf8Length ?? Utf8Length;
    internal int WriteEvidenceUtf8(IBufferWriter<byte> output) =>
        _receivedMessage?.WriteUtf8(output) ?? WriteUtf8(output);

    public SseEvent? Event { get; }
    public bool BoundaryComplete { get; }
    public bool Malformed { get; }
    public string Type { get; }
    public bool IsDone { get; }
    public bool IsResponseCompleted { get; }
    public bool IsSuccessfulResponseCompleted { get; }
    public bool IsError { get; }
    public bool IsTerminal { get; }

    public bool IsSemantic
    {
        get
        {
            if (Event is null)
            {
                return false;
            }
            if (Type is not ("ping" or "heartbeat"))
            {
                return true;
            }
            if (Event.Data is not IReadOnlyDictionary<string, object?> envelope) return true;
            foreach (var key in envelope.Keys)
                if (key != "type") return true;
            return false;
        }
    }

    public static SseFrame Done { get; } = new("data: [DONE]\n\n", new SseEvent("message", "[DONE]"));

    private static readonly JsonSerializerOptions GeneratedJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private SseFrame(SseEvent snapshot, string? eventName) : this("", snapshot)
    {
        _generatedEventName = eventName;
        if (GeneratedSseSerialization.IsSmall(snapshot.Data, eventName))
            _generatedUtf8 = new Lazy<byte[]>(() => GeneratedSseSerialization.Encode(snapshot.Data, eventName));
        _generatedWire = new Lazy<string>(() => _generatedUtf8 is not null
            ? System.Text.Encoding.UTF8.GetString(_generatedUtf8.Value)
            : GeneratedSseSerialization.ToWire(snapshot.Data, eventName));
    }

    public static SseFrame FromJson(IReadOnlyDictionary<string, object?> payload, string? eventName = null) =>
        new(new SseEvent(eventName ?? "message", Freeze(payload)), eventName);

    public static SseFrame FromOwnedJson(IReadOnlyDictionary<string, object?> payload, string? eventName = null) =>
        new(new SseEvent(eventName ?? "message", payload), eventName);

    private SseFrame(ReceivedJsonMessage received, string eventName)
        : this("", new SseEvent(eventName, received.Payload))
    {
        _receivedMessage = received;
        // Only legacy wire consumers need an SSE adapter. Source evidence reads the original
        // bytes directly and never forces this default-encoder serialization or applies M to it.
        _receivedWire = new Lazy<string>(() =>
        {
            using var source = received.OpenRead();
            using var document = JsonDocument.Parse(source);
            // Own has the same scalar/object mapping as the receiver. Reading immutable source
            // bytes preserves the old frozen wire even if a semantic consumer mutates Payload.
            return $"event: {eventName}\ndata: {JsonSerializer.Serialize(Own(document.RootElement))}\n\n";
        });
    }

    internal static SseFrame FromReceivedJson(ReceivedJsonMessage received, string eventName)
    {
        ArgumentNullException.ThrowIfNull(received);
        return new(received, eventName);
    }

    internal string? DiagnosticDataPrefix(int maxCharacters)
    {
        if (_generatedUtf8 is not null && !_generatedWire!.IsValueCreated)
        {
            var bytes = _generatedUtf8.Value;
            var start = bytes.AsSpan().IndexOf("data:"u8);
            if (start < 0) return null;
            var utf8Data = bytes.AsSpan(start, bytes.Length - start - 2);
            if (Encoding.UTF8.GetCharCount(utf8Data) <= maxCharacters) return Encoding.UTF8.GetString(utf8Data);
            if (maxCharacters <= 0) return "";
            var chars = ArrayPool<char>.Shared.Rent(maxCharacters + 1);
            try
            {
                // One extra UTF16 slot permits a prefix ending between a surrogate pair.
                Encoding.UTF8.GetDecoder().Convert(utf8Data, chars.AsSpan(0, maxCharacters + 1), true, out _, out _, out _);
                return new string(chars, 0, maxCharacters);
            }
            finally { ArrayPool<char>.Shared.Return(chars, clearArray: true); }
        }
        if (_generatedWire is not null && !_generatedWire.IsValueCreated)
        {
            return GeneratedSseSerialization.DiagnosticPrefix(Event!.Data, maxCharacters);
        }
        var wire = WireText.AsSpan().Trim();
        var data = wire.IndexOf("data:", StringComparison.Ordinal);
        return data < 0 ? null : wire.Slice(data, Math.Min(wire.Length - data, maxCharacters)).ToString();
    }

    internal bool IsGenerated => _generatedWire is not null;
    internal bool IsWireMaterialized => _receivedWire?.IsValueCreated ?? _generatedWire?.IsValueCreated ?? true;

    private static object? Freeze(object? value, int depth = 0)
    {
        if (depth > 64)
        {
            throw new JsonException("Generated event exceeds the JSON depth limit or contains a cycle.");
        }
        if (value is IReadOnlyDictionary<string, object?> dictionary)
        {
            var snapshot = new Dictionary<string, object?>(dictionary.Count, StringComparer.Ordinal);
            foreach (var pair in dictionary) snapshot.Add(pair.Key, Freeze(pair.Value, depth + 1));
            return snapshot;
        }
        if (value is IReadOnlyList<object?> items)
        {
            var snapshot = new List<object?>(items.Count);
            foreach (var item in items) snapshot.Add(Freeze(item, depth + 1));
            return snapshot;
        }
        return value switch
        {
            null => null,
            string => value,
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            JsonElement element => Own(element),
            _ => Own(JsonSerializer.SerializeToElement(value, GeneratedJson))
        };
    }

    internal static SseFrame Decode(string wire, string name, ReadOnlyMemory<char>? data, bool complete)
    {
        if (data is null)
        {
            return new(wire, null, complete);
        }
        if (data.Value.Span.SequenceEqual("[DONE]"))
        {
            return new(wire, new(name, "[DONE]"), complete);
        }
        try
        {
            using var document = JsonDocument.Parse(data.Value);
            return new(wire, new(name, Own(document.RootElement)), complete);
        }
        catch (JsonException)
        {
            return new(wire, new(name, data.Value.ToString()), complete, malformed: true);
        }
    }

    private static object? Own(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, object?>(value.GetPropertyCount(), StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) result.Add(property.Name, Own(property.Value));
            return result;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = new List<object?>(value.GetArrayLength());
            foreach (var item in value.EnumerateArray()) result.Add(Own(item));
            return result;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var n)
                ? (n is >= int.MinValue and <= int.MaxValue ? (object)(int)n : n)
                : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}

public sealed class ResponsesToolCallMapping
{
    public string ChatName { get; init; } = string.Empty;
    public string NativeType { get; init; } = "function";
    public string ResponsesName { get; init; } = string.Empty;
    public string? Namespace { get; init; }
    public bool IsAmbiguous { get; init; }
}

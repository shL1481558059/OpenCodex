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
    private readonly string _wireText;
    public string WireText => _generatedWire?.Value ?? _wireText;
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

            var length = _generatedUtf8 is not null
                ? _generatedUtf8.Value.Length
                : _generatedWire is not null && !_generatedWire.IsValueCreated
                    ? GeneratedSseSerialization.Count(Event!.Data, _generatedEventName)
                    : System.Text.Encoding.UTF8.GetByteCount(WireText);
            Interlocked.CompareExchange(ref _utf8Length, length, -1);
            return length;
        }
    }

    public int WriteUtf8(System.Buffers.IBufferWriter<byte> output) =>
        _generatedUtf8 is not null
            ? GeneratedSseSerialization.WriteUtf8(output, _generatedUtf8.Value.AsSpan())
            : _generatedWire is not null && !_generatedWire.IsValueCreated
                ? GeneratedSseSerialization.WriteUtf8(output, Event!.Data, _generatedEventName)
                : GeneratedSseSerialization.WriteUtf8(output, WireText);

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
    internal bool IsWireMaterialized => _generatedWire?.IsValueCreated ?? true;

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

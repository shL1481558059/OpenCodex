using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenCodex.Core.Protocols;

// Kept in the original namespace for source compatibility. Data owns managed values, never a
// borrowed JsonElement. Pipeline consumers must treat this event graph as read-only.
public sealed class SseEvent(string eventName, object? data)
{
    public string EventName { get; } = eventName;
    public object? Data { get; } = data;
}

/// <summary>A complete SSE wire frame and its one decoded data event (or a comment/heartbeat).</summary>
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
    private readonly string _wireText;
    public string WireText => _generatedWire?.Value ?? _wireText;
    private int _utf8Length = -1;
    public int Utf8Length
    {
        get
        {
            var cached = Volatile.Read(ref _utf8Length);
            if (cached >= 0) return cached;
            var length = System.Text.Encoding.UTF8.GetByteCount(WireText);
            Interlocked.CompareExchange(ref _utf8Length, length, -1);
            return length;
        }
    }
    public SseEvent? Event { get; }
    public bool BoundaryComplete { get; }
    public bool Malformed { get; }
    public string Type { get; }
    public bool IsDone { get; }
    public bool IsResponseCompleted { get; }
    public bool IsSuccessfulResponseCompleted { get; }
    public bool IsError { get; }
    public bool IsTerminal { get; }
    // Commitment is deliberately broader than TTFT: IDs, roles, tool starts and unknown
    // business events may already change client state. Comments/heartbeat alone do not.
    public bool IsSemantic
    {
        get
        {
            if(Event is null)return false;
            if(Type is not ("ping" or "heartbeat"))return true;
            // A provider cannot smuggle roles/tools/errors under a ping discriminator and
            // regain retry eligibility. Only an otherwise empty heartbeat envelope is exempt.
            return Event.Data is not IReadOnlyDictionary<string,object?> envelope
                || envelope.Keys.Any(key=>key!="type");
        }
    }

    public static SseFrame Done { get; } = new("data: [DONE]\n\n", new SseEvent("message", "[DONE]"));

    private static readonly JsonSerializerOptions GeneratedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private SseFrame(SseEvent snapshot, string? eventName) : this("", snapshot)
    {
        _generatedWire = new Lazy<string>(() =>
        {
            var data = JsonSerializer.Serialize(snapshot.Data, GeneratedJson);
            return eventName is null ? $"data: {data}\n\n" : $"event: {eventName}\ndata: {data}\n\n";
        });
    }

    /// <summary>Capture a generated event before its producer mutates the next round/item.
    /// Freeze the graph without an encode/parse round-trip; wire serialization is lazy/once.
    /// Consumers must still treat Event.Data as read-only, like decoded upstream events.</summary>
    public static SseFrame FromJson(IReadOnlyDictionary<string, object?> payload, string? eventName = null) =>
        new(new SseEvent(eventName ?? "message", Freeze(payload)), eventName);

    internal bool IsWireMaterialized => _generatedWire?.IsValueCreated ?? true;

    private static object? Freeze(object? value, int depth = 0)
    {
        if (depth > 64) throw new JsonException("Generated event exceeds the JSON depth limit or contains a cycle.");
        return value switch
        {
            null => null,
            string => value,
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            JsonElement element => Own(element),
            IReadOnlyDictionary<string, object?> dictionary => dictionary.ToDictionary(p => p.Key, p => Freeze(p.Value, depth + 1), StringComparer.Ordinal),
            IReadOnlyList<object?> items => items.Select(item => Freeze(item, depth + 1)).ToList(),
            // Extension DTOs / typed dictionaries / value-type arrays normalize to JSON values
            // once. Do not mistake IDictionary<string,string> for a JSON array of KeyValuePairs.
            _ => Own(JsonSerializer.SerializeToElement(value, GeneratedJson))
        };
    }

    internal static SseFrame Decode(string wire, string name, string? data, bool complete)
    {
        if (data is null) return new(wire, null, complete);
        if (data == "[DONE]") return new(wire, new(name, data), complete);
        try
        {
            using var document = JsonDocument.Parse(data);
            return new(wire, new(name, Own(document.RootElement)), complete);
        }
        catch (JsonException) { return new(wire, new(name, data), complete, malformed: true); }
    }

    private static object? Own(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => Own(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(Own).ToList(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var n)
            ? (n is >= int.MinValue and <= int.MaxValue ? (object)(int)n : n) : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}

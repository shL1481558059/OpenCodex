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
        WireText = wireText;
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

    public string WireText { get; }
    public SseEvent? Event { get; }
    public bool BoundaryComplete { get; }
    public bool Malformed { get; }
    public string Type { get; }
    public bool IsDone { get; }
    public bool IsResponseCompleted { get; }
    public bool IsSuccessfulResponseCompleted { get; }
    public bool IsError { get; }
    public bool IsTerminal { get; }

    public static SseFrame Done { get; } = new("data: [DONE]\n\n", new SseEvent("message", "[DONE]"));

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

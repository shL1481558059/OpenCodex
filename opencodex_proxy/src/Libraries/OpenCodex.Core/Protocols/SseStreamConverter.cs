using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public sealed class ConvertedStreamResult
{
    public Dictionary<string, object?>? UpstreamResponse { get; set; }
}

public static partial class SseStreamConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public sealed class SseEvent
{
    public SseEvent(string eventName, object? data)
    {
        EventName = eventName;
        Data = data;
    }

    public string EventName { get; }

    public object? Data { get; }
}

internal sealed class ToolCallAggregate
{
    public string? Id { get; set; }
    public string Type { get; set; } = "function";
    public string? Name { get; set; }
    public string Arguments { get; set; } = string.Empty;
}

internal sealed class ToolStreamState
{
    public int? OutputIndex { get; set; }
    public string? ItemId { get; set; }
    public bool ItemAdded { get; set; }
    public int StreamedArgumentsLength { get; set; }
}

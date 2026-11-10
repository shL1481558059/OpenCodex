namespace OpenCodex.Core.Protocols;

/// <summary>
/// A route attempt's converted request and immutable response name contract. Never cache across requests.
/// </summary>
public sealed class RequestConversionResult
{
    internal RequestConversionResult(
        Dictionary<string, object?> request,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? toolCallMappings)
    {
        Request = request;
        ToolCallMappings = toolCallMappings;
    }

    public Dictionary<string, object?> Request { get; }
    public IReadOnlyDictionary<string, ResponsesToolCallMapping>? ToolCallMappings { get; }
}

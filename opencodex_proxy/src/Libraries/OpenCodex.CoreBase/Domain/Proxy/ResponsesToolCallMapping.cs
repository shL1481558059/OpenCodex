// Shared by attempt contexts and the converter. Keep the public namespace for source compatibility.
namespace OpenCodex.Core.Protocols;

public sealed class ResponsesToolCallMapping
{
    public string ChatName { get; init; } = string.Empty;
    public string NativeType { get; init; } = "function";
    public string ResponsesName { get; init; } = string.Empty;
    public string? Namespace { get; init; }
}

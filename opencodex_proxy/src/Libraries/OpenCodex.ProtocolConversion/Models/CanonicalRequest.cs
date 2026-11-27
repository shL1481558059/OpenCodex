using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCodex.ProtocolConversion.Models;

public sealed class CanonicalRequest
{
    public ProtocolKind? SourceProtocol { get; init; }
    public required string Model { get; init; }
    public IList<CanonicalMessage> Messages { get; init; } = [];
    public IList<CanonicalToolDefinition> Tools { get; init; } = [];
    public JsonElement? ToolChoice { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Parameters { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class CanonicalMessage
{
    public required string Role { get; init; }
    public IList<CanonicalContentPart> Content { get; init; } = [];
    public IList<CanonicalToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? Name { get; init; }
    public string? NativeType { get; init; }
    public string? ReasoningContent { get; init; }
    public string? AnthropicThinkingEncrypted { get; init; }
    public bool? IsError { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class CanonicalContentPart
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? ImageUrl { get; init; }
    public string? Detail { get; init; }
    public string? FileId { get; init; }
    public string? FileData { get; init; }
    public string? FileName { get; init; }
    public string? FileUrl { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class CanonicalToolDefinition
{
    public required string Name { get; init; }
    public string Type { get; init; } = "function";
    public string? Namespace { get; init; }
    public string? NativeType { get; init; }
    public JsonElement? InputSchema { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class CanonicalToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Namespace { get; init; }
    public JsonElement Arguments { get; init; }
    public string? NativeType { get; init; }
    public Dictionary<string, JsonElement>? FunctionExtensionData { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

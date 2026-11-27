using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCodex.ProtocolConversion.Models;

public sealed class CanonicalResponse
{
    public ProtocolKind? SourceProtocol { get; init; }
    public required string Id { get; init; }
    public string? Model { get; init; }
    public long? Created { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Reasoning { get; init; } = string.Empty;
    public string Refusal { get; init; } = string.Empty;
    public IList<JsonElement> Annotations { get; init; } = [];
    public IList<CanonicalToolCall> ToolCalls { get; init; } = [];
    public IList<CanonicalToolResult> ToolResults { get; init; } = [];
    public string? Status { get; init; }
    public required string FinishReason { get; init; }
    public CanonicalUsage Usage { get; init; } = new();
    public string? AnthropicThinkingEncrypted { get; init; }
    public JsonElement? Raw { get; init; }
    public JsonElement? Error { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class CanonicalToolResult
{
    public required string Id { get; init; }
    public JsonElement Output { get; init; }
    public bool? IsError { get; init; }
    public string? NativeType { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class CanonicalUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens { get; init; }
    public int CachedTokens { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

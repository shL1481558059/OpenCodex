using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    public const string Responses = "responses";
    public const string Chat = "chat";
    public const string Messages = "messages";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> ResponsesToolCallTypes =
    [
        "function_call",
        "custom_tool_call",
        "local_shell_call",
        "shell_call",
        "apply_patch_call"
    ];

    private static readonly HashSet<string> ResponsesToolOutputTypes =
    [
        "function_call_output",
        "custom_tool_call_output",
        "local_shell_call_output",
        "shell_call_output",
        "apply_patch_call_output",
        "tool_result",
        "tool_search_output"
    ];

    private const string NamespaceSeparator = "__";
    private const string LegacyNamespaceSeparator = ".";
    private const string MissingToolOutputMessage = "[tool output missing - no function_call_output was provided for this call_id]";
    private const string PlanModeTagInstruction = """
                                                  You are currently in Codex Plan Mode.

                                                  If you present an official plan, your entire final answer must be wrapped exactly like this:

                                                  <proposed_plan>
                                                  ...markdown plan...
                                                  </proposed_plan>

                                                  The opening and closing tags must each be on their own line. Do not translate the tags. The client will not recognize the plan without these tags.
                                                  """;

    internal static TextFormatInfo? ExtractTextFormat(Dictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("text", out var textObj) || textObj is not Dictionary<string, object?> text)
        {
            return null;
        }

        if (!text.TryGetValue("format", out var formatObj) || formatObj is not Dictionary<string, object?> format)
        {
            return null;
        }

        var type = format.TryGetValue("type", out var typeVal) ? typeVal?.ToString() : null;
        if (type != "json_schema")
        {
            return null;
        }

        var name = format.TryGetValue("name", out var nameVal) ? nameVal?.ToString() : null;
        Dictionary<string, object?>? schema = null;
        if (format.TryGetValue("schema", out var schemaObj) && schemaObj is Dictionary<string, object?> schemaDict)
        {
            schema = schemaDict;
        }

        return new TextFormatInfo
        {
            Type = type,
            SchemaName = name,
            Schema = schema
        };
    }

    private static bool IsResponsesToolCallLike(Dictionary<string, object?> item)
    {
        var type = GetString(item, "type");
        if (string.IsNullOrEmpty(type) || type == "web_search_call")
        {
            return false;
        }

        if (ResponsesToolCallTypes.Contains(type))
        {
            return true;
        }

        if (!type.EndsWith("_call", StringComparison.Ordinal))
        {
            return false;
        }

        var hasCallIdentity = HasNonNullValue(item, "call_id") || HasNonNullValue(item, "id");
        var hasInvocationShape = HasNonNullValue(item, "name")
            || HasNonNullValue(item, "arguments")
            || HasNonNullValue(item, "input")
            || HasNonNullValue(item, "action");
        return hasCallIdentity && hasInvocationShape;
    }

    private static bool IsResponsesToolOutputLike(Dictionary<string, object?> item)
    {
        var type = GetString(item, "type");
        if (string.IsNullOrEmpty(type))
        {
            return false;
        }

        if (ResponsesToolOutputTypes.Contains(type))
        {
            return true;
        }

        if (!type.EndsWith("_call_output", StringComparison.Ordinal))
        {
            return false;
        }

        var hasCallIdentity = HasNonNullValue(item, "call_id")
            || HasNonNullValue(item, "tool_call_id")
            || HasNonNullValue(item, "tool_use_id");
        var hasOutput = HasNonNullValue(item, "output") || HasNonNullValue(item, "content");
        return hasCallIdentity && hasOutput;
    }

    private static string ResponsesToolCallName(Dictionary<string, object?> item)
    {
        var type = GetString(item, "type") ?? string.Empty;
        if (HasNonNullValue(item, "name"))
        {
            return GetString(item, "name") ?? string.Empty;
        }

        return type.EndsWith("_call", StringComparison.Ordinal)
            ? type[..^"_call".Length]
            : type;
    }

    private static object? ResponsesToolCallArguments(Dictionary<string, object?> item)
    {
        return GetValue(item, "arguments")
            ?? GetValue(item, "input")
            ?? GetValue(item, "action")
            ?? new Dictionary<string, object?>();
    }

    public static bool SupportsStreamingConversion(string clientProtocol, string upstreamProtocol)
    {
        return SseStreamConverter.SupportsUpstreamStreamConversion(clientProtocol, upstreamProtocol);
    }

    /// <summary>
    /// Converts a client request into the request sent to the upstream channel.
    /// This entry point owns and deep-copies the caller's payload graph.
    /// </summary>
    public static Dictionary<string, object?> ConvertClientRequestToUpstream(
        Dictionary<string, object?> payload,
        string clientProtocol,
        string upstreamProtocol,
        string upstreamModel,
        IReadOnlyDictionary<string, object?>? compat = null)
    {
        return ConvertClientRequestToUpstreamWithMetadata(
            payload,
            clientProtocol,
            upstreamProtocol,
            upstreamModel,
            compat).Request;
    }

    /// <summary>
    /// Converts a client request into the upstream request and returns the route-owned
    /// tool-name mappings produced by that conversion.
    /// This entry point owns and deep-copies the caller's payload graph.
    /// </summary>
    public static RequestConversionResult ConvertClientRequestToUpstreamWithMetadata(
        Dictionary<string, object?> payload,
        string clientProtocol,
        string upstreamProtocol,
        string upstreamModel,
        IReadOnlyDictionary<string, object?>? compat = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ConvertRequestGraph(
            AsObject(DeepCopy(payload)),
            clientProtocol,
            upstreamProtocol,
            upstreamModel,
            compat);
    }

    // Compatibility entry for tests and existing callers. Production code uses the
    // direction-explicit method above.
    internal static Dictionary<string, object?> ConvertRequest(
        Dictionary<string, object?> payload,
        string clientProtocol,
        string upstreamProtocol,
        string upstreamModel,
        IReadOnlyDictionary<string, object?>? compat = null)
    {
        return ConvertClientRequestToUpstream(
            payload,
            clientProtocol,
            upstreamProtocol,
            upstreamModel,
            compat);
    }

    // Compatibility entry for tests and existing callers. Production code uses the
    // direction-explicit method above.
    internal static RequestConversionResult ConvertRequestWithMetadata(
        Dictionary<string, object?> payload,
        string clientProtocol,
        string upstreamProtocol,
        string upstreamModel,
        IReadOnlyDictionary<string, object?>? compat = null)
    {
        return ConvertClientRequestToUpstreamWithMetadata(
            payload,
            clientProtocol,
            upstreamProtocol,
            upstreamModel,
            compat);
    }

    // For the endpoint's exclusively owned attempt graph only. A shallow root keeps the
    // working payload's model intact; nested containers already belong to this attempt.
    // Public conversion APIs continue to deep-copy caller-owned graphs.
    internal static RequestConversionResult ConvertOwnedClientRequestToUpstreamWithMetadata(
        Dictionary<string, object?> payload,
        string clientProtocol,
        string upstreamProtocol,
        string upstreamModel,
        IReadOnlyDictionary<string, object?>? compat = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ConvertRequestGraph(
            new Dictionary<string, object?>(payload, StringComparer.Ordinal),
            clientProtocol,
            upstreamProtocol,
            upstreamModel,
            compat);
    }

    // Compatibility entry for tests. Production code uses the direction-explicit method above.
    internal static RequestConversionResult ConvertOwnedRequestWithMetadata(
        Dictionary<string, object?> payload,
        string clientProtocol,
        string upstreamProtocol,
        string upstreamModel,
        IReadOnlyDictionary<string, object?>? compat = null)
    {
        return ConvertOwnedClientRequestToUpstreamWithMetadata(
            payload,
            clientProtocol,
            upstreamProtocol,
            upstreamModel,
            compat);
    }

    private static RequestConversionResult ConvertRequestGraph(
        Dictionary<string, object?> converted,
        string clientProtocol,
        string upstreamProtocol,
        string upstreamModel,
        IReadOnlyDictionary<string, object?>? compat)
    {
        converted["model"] = upstreamModel;
        if (clientProtocol == upstreamProtocol)
        {
            if (clientProtocol != Responses)
            {
                SanitizeRequestToolSchemas(converted, upstreamProtocol);
                SanitizeRequestToolChoiceConsistency(converted);
            }
            return new RequestConversionResult(converted, null);
        }

        ValidateRequestSemanticCompatibility(converted, clientProtocol, upstreamProtocol);
        var canonicalTools = clientProtocol == Responses && upstreamProtocol is Chat or Messages
            ? ResponsesRequestToolsToCanonical(converted, compat)
            : null;
        var toolWireNamePlan = canonicalTools is null ? null : BuildResponsesToolWireNamePlan(canonicalTools);
        var canonical = ToCanonicalRequest(converted, clientProtocol, compat, toolWireNamePlan, canonicalTools);
        var result = FromCanonicalRequest(canonical, upstreamProtocol, toolWireNamePlan);
        SanitizeRequestToolChoiceConsistency(result);
        return new RequestConversionResult(result, toolWireNamePlan?.Mappings);
    }

    /// <summary>
    /// Converts an upstream response into the response returned to the client.
    /// This entry point owns and deep-copies the caller's payload graph.
    /// </summary>
    public static Dictionary<string, object?> ConvertUpstreamResponseToClient(
        Dictionary<string, object?> payload,
        string upstreamProtocol,
        string clientProtocol,
        string? originalModel,
        TextFormatInfo? textFormat = null,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? toolCallMappings = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ConvertUpstreamResponseCore(AsObject(DeepCopy(payload)), upstreamProtocol, clientProtocol,
            originalModel, textFormat, toolCallMappings);
    }

    // Compatibility entry for tests and existing callers. Production code uses the
    // direction-explicit method above.
    internal static Dictionary<string, object?> ConvertResponse(
        Dictionary<string, object?> payload,
        string clientProtocol,
        string upstreamProtocol,
        string? originalModel,
        TextFormatInfo? textFormat = null,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? toolCallMappings = null)
    {
        return ConvertUpstreamResponseToClient(
            payload,
            upstreamProtocol,
            clientProtocol,
            originalModel,
            textFormat,
            toolCallMappings);
    }

    /// <summary>
    /// Converts an upstream response for the endpoint's exclusively owned attempt graph.
    /// A shallow root preserves the attempt-owned graph; nested containers already belong to it.
    /// </summary>
    internal static Dictionary<string, object?> ConvertOwnedUpstreamResponseToClient(
        Dictionary<string, object?> payload,
        string upstreamProtocol,
        string clientProtocol,
        string? originalModel,
        TextFormatInfo? textFormat = null,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? toolCallMappings = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ConvertUpstreamResponseCore(
            new Dictionary<string, object?>(payload, StringComparer.Ordinal),
            upstreamProtocol,
            clientProtocol,
            originalModel,
            textFormat,
            toolCallMappings);
    }

    // Compatibility entry for tests. Production code uses the direction-explicit method above.
    internal static Dictionary<string, object?> ConvertOwnedResponse(
        Dictionary<string, object?> payload,
        string clientProtocol,
        string upstreamProtocol,
        string? originalModel,
        TextFormatInfo? textFormat = null,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? toolCallMappings = null)
    {
        return ConvertOwnedUpstreamResponseToClient(
            payload,
            upstreamProtocol,
            clientProtocol,
            originalModel,
            textFormat,
            toolCallMappings);
    }

    private static Dictionary<string, object?> ConvertUpstreamResponseCore(
        Dictionary<string, object?> converted,
        string upstreamProtocol,
        string clientProtocol,
        string? originalModel,
        TextFormatInfo? textFormat,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? toolCallMappings)
    {
        if (upstreamProtocol == clientProtocol)
        {
            if (!string.IsNullOrEmpty(originalModel))
            {
                converted["model"] = originalModel;
            }

            return converted;
        }

        var canonical = ToCanonicalResponse(converted, upstreamProtocol, originalModel, toolCallMappings);
        var result = FromCanonicalResponse(canonical, clientProtocol);

        if (textFormat is { Type: "json_schema" } && clientProtocol == Responses)
        {
            result = ApplyJsonSchemaTextFormat(result, textFormat);
        }

        return result;
    }

    internal static Dictionary<string, object?> ApplyJsonSchemaTextFormat(
        Dictionary<string, object?> responsePayload,
        TextFormatInfo textFormat)
    {
        if (!responsePayload.TryGetValue("output", out var outputObj) || outputObj is not List<object?> output)
        {
            return responsePayload;
        }

        for (var i = 0; i < output.Count; i++)
        {
            if (output[i] is not Dictionary<string, object?> item || GetString(item, "type") != "message")
            {
                continue;
            }

            if (!item.TryGetValue("content", out var contentObj) || contentObj is not List<object?> content)
            {
                continue;
            }

            for (var j = 0; j < content.Count; j++)
            {
                if (content[j] is not Dictionary<string, object?> part || GetString(part, "type") != "output_text")
                {
                    continue;
                }

                var text = GetString(part, "text");
                if (text is null || text.Length == 0)
                {
                    continue;
                }

                var wrapped = SseStreamConverter.WrapTextForJsonSchema(text, textFormat);
                if (!ReferenceEquals(wrapped, text))
                {
                    content[j] = new Dictionary<string, object?>(part, StringComparer.Ordinal)
                    {
                        ["text"] = wrapped
                    };
                }
            }
        }

        return responsePayload;
    }
}

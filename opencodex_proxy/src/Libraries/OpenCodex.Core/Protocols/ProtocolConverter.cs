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
        "tool_result"
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

    public static bool SupportsStreamingConversion(string sourceProtocol, string targetProtocol)
    {
        return sourceProtocol == targetProtocol
            || (sourceProtocol == Responses
                && targetProtocol is Chat or Messages);
    }

    public static Dictionary<string, object?> ConvertRequest(
        Dictionary<string, object?> payload,
        string sourceProtocol,
        string targetProtocol,
        string upstreamModel)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var converted = AsObject(DeepCopy(payload));
        converted["model"] = upstreamModel;
        if (sourceProtocol == targetProtocol)
        {
            SanitizeRequestToolSchemas(converted, targetProtocol);
            return converted;
        }

        var canonical = ToCanonicalRequest(converted, sourceProtocol);
        return FromCanonicalRequest(canonical, targetProtocol);
    }

    public static Dictionary<string, object?> ConvertResponse(
        Dictionary<string, object?> payload,
        string sourceProtocol,
        string targetProtocol,
        string? originalModel,
        TextFormatInfo? textFormat = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (sourceProtocol == targetProtocol)
        {
            var converted = AsObject(DeepCopy(payload));
            if (!string.IsNullOrEmpty(originalModel))
            {
                converted["model"] = originalModel;
            }

            return converted;
        }

        var canonical = ToCanonicalResponse(payload, targetProtocol, originalModel);
        var result = FromCanonicalResponse(canonical, sourceProtocol);

        if (textFormat is { Type: "json_schema" } && sourceProtocol == Responses)
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

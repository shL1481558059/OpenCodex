using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenCodex.Api.Protocols;

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
            return converted;
        }

        var canonical = ToCanonicalRequest(converted, sourceProtocol);
        return FromCanonicalRequest(canonical, targetProtocol);
    }

    public static Dictionary<string, object?> ConvertResponse(
        Dictionary<string, object?> payload,
        string sourceProtocol,
        string targetProtocol,
        string? originalModel)
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
        return FromCanonicalResponse(canonical, sourceProtocol);
    }
}

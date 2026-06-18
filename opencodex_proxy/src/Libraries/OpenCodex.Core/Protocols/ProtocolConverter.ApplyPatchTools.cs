using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    internal static Dictionary<string, object?> ResponsesToolCallItemFromToolCall(
        object? callId,
        object? name,
        object? arguments,
        object? namespaceValue = null,
        string? itemId = null)
    {
        var toolName = Convert.ToString(name) ?? string.Empty;

        var functionCall = Obj(
            ("id", itemId ?? NewId("fc")),
            ("type", "function_call"),
            ("status", "completed"),
            ("call_id", callId),
            ("arguments", JsonDumps(arguments ?? "{}")));
        MergeInto(functionCall, ResponsesFunctionCallNameFields(toolName, namespaceValue));
        return functionCall;
    }

    private static object? NormalizeApplyPatchArguments(string itemType, string name, object? arguments)
    {
        var normalizedName = name.Replace("-", "_", StringComparison.Ordinal);
        if (!IsApplyPatchName(normalizedName) && itemType != "apply_patch_call")
        {
            return arguments;
        }

        if (arguments is string text)
        {
            return IsJsonObjectString(text) ? text : Obj(("patch", text));
        }

        if (TryAsObject(arguments, out var dictionary))
        {
            if (dictionary.ContainsKey("patch"))
            {
                return dictionary;
            }

            if (dictionary.Count == 1 && dictionary.ContainsKey("input"))
            {
                return Obj(("patch", dictionary["input"]));
            }
        }

        return arguments;
    }

    private static bool IsJsonObjectString(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    public static bool IsApplyPatchPublic(string name)
    {
        var normalized = name.Replace("-", "_", StringComparison.Ordinal);
        return IsApplyPatchName(normalized);
    }

    internal static Dictionary<string, object?> ResponsesToolCallItemFromToolCall(
        object? callId,
        object? name,
        object? arguments,
        object? namespaceValue = null,
        string? itemId = null)
    {
        var toolName = Convert.ToString(name) ?? string.Empty;
        var serializedArguments = JsonDumps(arguments ?? "{}");
        var callKind = GetResponsesToolCallKind(toolName);

        if (callKind == ResponsesToolCallKind.CustomTool)
        {
            serializedArguments = ExtractPatchText(serializedArguments) ?? serializedArguments;
        }

        if (callKind == ResponsesToolCallKind.CustomTool)
        {
            var customToolCall = Obj(
                ("id", itemId ?? NewId("tc")),
                ("type", "custom_tool_call"),
                ("status", "completed"),
                ("call_id", callId),
                ("input", serializedArguments));
            MergeInto(customToolCall, ResponsesFunctionCallNameFields(toolName, namespaceValue));
            return customToolCall;
        }

        var functionCall = Obj(
            ("id", itemId ?? NewId("fc")),
            ("type", "function_call"),
            ("status", "completed"),
            ("call_id", callId),
            ("arguments", serializedArguments));
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

    private static string? ExtractPatchText(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is "patch" or "input" or "command"
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return arguments;
        }

        return null;
    }
}

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
        string? itemId = null,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? mappings = null)
    {
        var toolName = Convert.ToString(name) ?? string.Empty;
        var serializedArguments = JsonDumps(arguments ?? "{}");
        var shape = ResolveResponsesToolCallShape(toolName, mappings);
        var responseName = string.IsNullOrEmpty(shape.Name) ? toolName : shape.Name;
        var responseNamespace = shape.Namespace ?? Convert.ToString(namespaceValue);

        if (shape.Kind == ResponsesToolCallKind.CustomTool)
        {
            serializedArguments = ExtractPatchText(serializedArguments) ?? serializedArguments;
        }

        if (shape.Kind == ResponsesToolCallKind.CustomTool)
        {
            var customToolCall = Obj(
                ("id", itemId ?? NewId("tc")),
                ("type", shape.ItemType),
                ("status", "completed"),
                ("call_id", callId),
                ("input", serializedArguments));
            MergeInto(customToolCall, ResponsesFunctionCallNameFields(responseName, responseNamespace));
            return customToolCall;
        }

        if (shape.Kind == ResponsesToolCallKind.NativeTool)
        {
            var nativeToolCall = Obj(
                ("id", itemId ?? NewId("tc")),
                ("type", shape.ItemType),
                ("status", "completed"),
                ("call_id", callId),
                (shape.ArgumentField, serializedArguments));
            MergeInto(nativeToolCall, ResponsesFunctionCallNameFields(responseName, responseNamespace));
            return nativeToolCall;
        }

        var functionCall = Obj(
            ("id", itemId ?? NewId("fc")),
            ("type", shape.ItemType),
            ("status", "completed"),
            ("call_id", callId),
            ("arguments", serializedArguments));
        MergeInto(functionCall, ResponsesFunctionCallNameFields(responseName, responseNamespace));
        return functionCall;
    }

    internal static Dictionary<string, object?> ResponsesToolCallStartedItem(
        object? callId,
        object? name,
        string itemId,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? mappings = null)
    {
        var toolName = Convert.ToString(name) ?? string.Empty;
        var shape = ResolveResponsesToolCallShape(toolName, mappings);
        var responseName = string.IsNullOrEmpty(shape.Name) ? toolName : shape.Name;
        var item = Obj(
            ("id", itemId),
            ("type", shape.ItemType),
            ("status", "in_progress"),
            ("call_id", callId),
            (shape.ArgumentField, string.Empty));
        MergeInto(item, ResponsesFunctionCallNameFields(responseName, shape.Namespace));
        return item;
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

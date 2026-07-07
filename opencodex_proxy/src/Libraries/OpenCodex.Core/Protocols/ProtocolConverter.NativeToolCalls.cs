namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
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

        if (shape.Kind == ResponsesToolCallKind.NativeTool && IsWebSearchName(responseName))
        {
            return ResponsesWebSearchCallItem(callId, arguments, itemId);
        }

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
        if (shape.Kind == ResponsesToolCallKind.NativeTool && IsWebSearchName(responseName))
        {
            return ResponsesWebSearchCallStartedItem(callId, itemId);
        }

        var item = Obj(
            ("id", itemId),
            ("type", shape.ItemType),
            ("status", "in_progress"),
            ("call_id", callId),
            (shape.ArgumentField, string.Empty));
        MergeInto(item, ResponsesFunctionCallNameFields(responseName, shape.Namespace));
        return item;
    }
}

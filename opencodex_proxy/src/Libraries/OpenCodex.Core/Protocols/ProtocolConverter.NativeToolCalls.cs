using System.Text.Json;
using OpenCodex.Core.Errors;

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
        if (mappings is not null && namespaceValue is not null
            && !string.Equals(Convert.ToString(namespaceValue), shape.Namespace ?? string.Empty, StringComparison.Ordinal))
            throw new UpstreamException("Upstream tool call namespace conflicts with its declared tool identity.");
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
            MergeInto(customToolCall, DeclaredToolCallNameFields(toolName, responseName, responseNamespace, mappings));
            return customToolCall;
        }

        if (shape.Kind == ResponsesToolCallKind.NativeTool)
        {
            object? nativeArguments = string.Equals(shape.ItemType, "tool_search_call", StringComparison.Ordinal)
                ? ParseToolSearchArguments(arguments)
                : serializedArguments;
            var nativeToolCall = Obj(
                ("id", itemId ?? NewId("tc")),
                ("type", shape.ItemType),
                ("status", "completed"),
                ("call_id", callId),
                (shape.ArgumentField, nativeArguments));
            MergeInto(nativeToolCall, DeclaredToolCallNameFields(toolName, responseName, responseNamespace, mappings));
            MergeNativeToolExecution(nativeToolCall);
            return nativeToolCall;
        }

        var functionCall = Obj(
            ("id", itemId ?? NewId("fc")),
            ("type", shape.ItemType),
            ("status", "completed"),
            ("call_id", callId),
            ("arguments", serializedArguments));
        MergeInto(functionCall, DeclaredToolCallNameFields(toolName, responseName, responseNamespace, mappings));
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
        MergeInto(item, DeclaredToolCallNameFields(toolName, responseName, shape.Namespace, mappings));
        MergeNativeToolExecution(item);
        return item;
    }

    private static Dictionary<string, object?> DeclaredToolCallNameFields(string upstreamName,
        string responseName, string? responseNamespace,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? mappings)
    {
        if (!TryGetResponsesToolCallMapping(upstreamName, mappings, out var mapping))
            return ResponsesFunctionCallNameFields(responseName, responseNamespace);
        var fields = Obj(("name", mapping.ResponsesName));
        if (!string.IsNullOrEmpty(mapping.Namespace)) fields["namespace"] = mapping.Namespace;
        return fields;
    }

    private static void MergeNativeToolExecution(Dictionary<string, object?> item)
    {
        if (string.Equals(GetString(item, "type"), "tool_search_call", StringComparison.Ordinal))
        {
            item["execution"] = "client";
        }
    }

    private static Dictionary<string, object?> ParseToolSearchArguments(object? arguments)
    {
        if (TryAsObject(arguments, out var argumentObject))
        {
            return AsObject(DeepCopy(argumentObject));
        }

        var text = Convert.ToString(arguments);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var parsed = FromJsonElement(document.RootElement);
            if (TryAsObject(parsed, out var parsedObject))
            {
                return parsedObject;
            }
        }
        catch (JsonException)
        {
            // Converted below into a stable protocol error with no raw payload leakage.
        }

        throw new BadRequestException("tool_search arguments must be a valid JSON object");
    }
}

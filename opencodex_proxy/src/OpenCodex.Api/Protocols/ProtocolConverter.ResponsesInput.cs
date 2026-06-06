namespace OpenCodex.Api.Protocols;

public static partial class ProtocolConverter
{
    private static List<object?> ResponsesInputItemToMessages(object? item)
    {
        if (item is string text)
        {
            return [Obj(("role", "user"), ("content", text))];
        }

        if (!TryAsObject(item, out var inputItem))
        {
            return [];
        }

        var itemType = GetString(inputItem, "type");
        if (itemType is not null && ResponsesToolCallTypes.Contains(itemType))
        {
            var name = GetString(inputItem, "name") ?? itemType.Replace("_call", string.Empty, StringComparison.Ordinal);
            var namespaceName = GetString(inputItem, "namespace");
            if (!string.IsNullOrEmpty(namespaceName))
            {
                name = $"{namespaceName}{NamespaceSeparator}{name}";
            }

            var arguments = GetValue(inputItem, "arguments")
                ?? GetValue(inputItem, "input")
                ?? GetValue(inputItem, "action")
                ?? new Dictionary<string, object?>();
            arguments = NormalizeApplyPatchArguments(itemType, name, arguments);
            return
            [
                Obj(
                    ("role", "assistant"),
                    ("content", string.Empty),
                    ("tool_calls", new List<object?>
                    {
                        Obj(
                            ("id", GetValue(inputItem, "call_id") ?? GetValue(inputItem, "id") ?? NewId("call")),
                            ("type", "function"),
                            ("function", Obj(("name", NamespaceNameToChat(name)), ("arguments", JsonDumps(arguments)))))
                    }))
            ];
        }

        if (itemType is not null && ResponsesToolOutputTypes.Contains(itemType))
        {
            var callId = GetValue(inputItem, "call_id") ?? GetValue(inputItem, "tool_call_id") ?? GetValue(inputItem, "tool_use_id");
            if (callId is null)
            {
                return [];
            }

            var output = GetValue(inputItem, "output") ?? GetValue(inputItem, "content") ?? string.Empty;
            return [Obj(("role", "tool"), ("tool_call_id", callId), ("content", StringifyContent(output)))];
        }

        if (itemType == "reasoning")
        {
            var reasoning = ResponsesReasoningToText(inputItem);
            return string.IsNullOrEmpty(reasoning)
                ? []
                : [Obj(("role", "assistant"), ("content", string.Empty), ("reasoning_content", reasoning))];
        }

        if (itemType == "web_search_call")
        {
            if (inputItem.ContainsKey("opencodex_result"))
            {
                var callId = GetValue(inputItem, "call_id") ?? GetValue(inputItem, "id") ?? NewId("call");
                var action = ObjectValue(inputItem, "action");
                var toolCall = Obj(
                    ("id", callId),
                    ("type", "function"),
                    ("function", Obj(
                        ("name", "web_search"),
                        ("arguments", JsonDumps(Obj(("query", GetValue(action, "query") ?? string.Empty)))))));
                return
                [
                    Obj(
                        ("role", "assistant"),
                        ("content", string.Empty),
                        ("tool_calls", new List<object?> { toolCall })),
                    Obj(
                        ("role", "tool"),
                        ("tool_call_id", callId),
                        ("content", JsonDumps(GetValue(inputItem, "opencodex_result") ?? new Dictionary<string, object?>())))
                ];
            }

            var metadataText = ResponsesMetadataItemToText(inputItem);
            return string.IsNullOrEmpty(metadataText) ? [] : [Obj(("role", "assistant"), ("content", metadataText))];
        }

        if (!string.IsNullOrEmpty(itemType) && !inputItem.ContainsKey("role") && !inputItem.ContainsKey("content"))
        {
            var metadataText = ResponsesMetadataItemToText(inputItem);
            return string.IsNullOrEmpty(metadataText) ? [] : [Obj(("role", "assistant"), ("content", metadataText))];
        }

        var role = GetString(inputItem, "role") ?? "user";
        if (role == "developer")
        {
            role = "system";
        }

        var content = ResponsesContentToChatContent(GetValue(inputItem, "content") ?? string.Empty);
        return IsEmptyChatContent(content) ? [] : [Obj(("role", role), ("content", content))];
    }

    private static (string Instructions, List<object?> InputItems) MessagesToResponsesInput(List<object?> messages)
    {
        var instructions = new List<string>();
        var inputItems = new List<object?>();
        foreach (var item in messages)
        {
            if (!TryAsObject(item, out var message))
            {
                continue;
            }

            var role = GetString(message, "role") ?? "user";
            if (role is "system" or "developer")
            {
                var text = StringifyContent(GetValue(message, "content") ?? string.Empty);
                if (!string.IsNullOrEmpty(text))
                {
                    instructions.Add(text);
                }

                continue;
            }

            var reasoningContent = StringifyContent(GetValue(message, "reasoning_content") ?? string.Empty).Trim();
            if (role == "assistant" && !string.IsNullOrEmpty(reasoningContent))
            {
                inputItems.Add(ResponsesReasoningItem(reasoningContent));
            }

            if (role == "tool")
            {
                inputItems.Add(Obj(
                    ("type", "function_call_output"),
                    ("call_id", GetValue(message, "tool_call_id")),
                    ("output", StringifyContent(GetValue(message, "content") ?? string.Empty))));
                continue;
            }

            inputItems.Add(Obj(
                ("type", "message"),
                ("role", role),
                ("content", ChatContentToResponsesContent(GetValue(message, "content") ?? string.Empty, role))));

            foreach (var toolCallItem in ListValue(message, "tool_calls"))
            {
                if (!TryAsObject(toolCallItem, out var toolCall))
                {
                    continue;
                }

                var function = ObjectValue(toolCall, "function");
                inputItems.Add(Obj(
                    ("type", "function_call"),
                    ("call_id", GetValue(toolCall, "id")),
                    ("arguments", GetValue(function, "arguments") ?? "{}"),
                    ("name", GetValue(function, "name") ?? string.Empty)));
            }
        }

        return (string.Join("\n\n", instructions), inputItems);
    }

    private static List<object?> MergeSystemMessages(List<object?> messages)
    {
        var systemParts = new List<string>();
        var nonSystem = new List<object?>();
        foreach (var item in messages)
        {
            if (!TryAsObject(item, out var message))
            {
                continue;
            }

            if (GetString(message, "role") == "system")
            {
                var content = StringifyContent(GetValue(message, "content") ?? string.Empty);
                if (!string.IsNullOrEmpty(content))
                {
                    systemParts.Add(content);
                }

                continue;
            }

            nonSystem.Add(message);
        }

        if (systemParts.Count == 0)
        {
            return nonSystem;
        }

        return [Obj(("role", "system"), ("content", string.Join("\n\n", systemParts))), .. nonSystem];
    }
}

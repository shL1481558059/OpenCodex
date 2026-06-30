namespace OpenCodex.Core.Protocols;

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
        if (IsResponsesToolCallLike(inputItem))
        {
            var name = ResponsesToolCallName(inputItem);
            var namespaceName = GetString(inputItem, "namespace");
            if (!string.IsNullOrEmpty(namespaceName))
            {
                name = $"{namespaceName}{NamespaceSeparator}{name}";
            }

            var arguments = ResponsesToolCallArguments(inputItem);
            arguments = EnrichMcpToolCallArguments(name, arguments);
            arguments = NormalizeApplyPatchArguments(itemType ?? string.Empty, name, arguments);
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

        if (IsResponsesToolOutputLike(inputItem))
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
            if (string.IsNullOrEmpty(reasoning))
            {
                return [];
            }

            var msg = Obj(("role", "assistant"), ("content", string.Empty), ("reasoning_content", reasoning));
            var encryptedContent = StringifyContent(GetValue(inputItem, "encrypted_content") ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(encryptedContent) && encryptedContent.StartsWith(AnthropicThinkingPrefix, StringComparison.Ordinal))
                msg["anthropic_thinking_encrypted"] = encryptedContent;
            return [msg];
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
            var anthropicThinkingEncrypted = GetString(message, "anthropic_thinking_encrypted") ?? string.Empty;
            if (role == "assistant" && !string.IsNullOrEmpty(reasoningContent))
            {
                // Prefer encoded thinking blocks (with signatures) over plain text
                if (!string.IsNullOrEmpty(anthropicThinkingEncrypted)
                    && anthropicThinkingEncrypted.StartsWith(AnthropicThinkingPrefix, StringComparison.Ordinal))
                {
                    inputItems.Add(Obj(
                        ("type", "reasoning"),
                        ("summary", new List<object?> { Obj(("type", "summary_text"), ("text", reasoningContent)) }),
                        ("encrypted_content", anthropicThinkingEncrypted),
                        ("status", "completed")));
                }
                else
                {
                    inputItems.Add(ResponsesReasoningItem(reasoningContent));
                }
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

    /// <summary>
    /// 临时修复：为 MCP 工具补全必需参数
    /// TODO: 迁移到专用的 McpToolCallEnricher 服务
    /// </summary>
    private static object? EnrichMcpToolCallArguments(string toolName, object? arguments)
    {
        // 只处理 MCP 工具
        if (!toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return arguments;
        }

        if (!TryAsObject(arguments, out var argsDict))
        {
            return arguments;
        }

        // 为 node_repl 工具补全 sandboxPolicy
        if (toolName.StartsWith("mcp__node_repl__", StringComparison.Ordinal) 
            && !argsDict.ContainsKey("sandboxPolicy"))
        {
            argsDict["sandboxPolicy"] = "use_default";
        }

        return argsDict;
    }
}

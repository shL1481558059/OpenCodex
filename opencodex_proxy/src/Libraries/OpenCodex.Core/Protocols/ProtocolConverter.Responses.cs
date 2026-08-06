using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static Dictionary<string, object?> ToCanonicalResponse(
        Dictionary<string, object?> payload,
        string protocol,
        string? originalModel,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? toolCallMappings)
    {
        return protocol switch
        {
            Responses => ResponsesResponseToCanonical(payload, originalModel),
            Chat => ChatResponseToCanonical(payload, originalModel, toolCallMappings),
            Messages => MessagesResponseToCanonical(payload, originalModel, toolCallMappings),
            _ => throw new BadRequestException($"unsupported upstream protocol: {protocol}")
        };
    }

    private static Dictionary<string, object?> FromCanonicalResponse(Dictionary<string, object?> canonical, string protocol)
    {
        return protocol switch
        {
            Responses => CanonicalToResponsesResponse(canonical),
            Chat => CanonicalToChatResponse(canonical),
            Messages => CanonicalToMessagesResponse(canonical),
            _ => throw new BadRequestException($"unsupported response protocol: {protocol}")
        };
    }

    private static Dictionary<string, object?> ResponsesResponseToCanonical(
        Dictionary<string, object?> payload,
        string? originalModel)
    {
        var textParts = new List<string>();
        var reasoningParts = new List<string>();
        var annotations = new List<object?>();
        var toolCalls = new List<object?>();
        var toolResults = new List<object?>();
        var refusalParts = new List<string>();

        foreach (var outputItem in ListValue(payload, "output"))
        {
            if (!TryAsObject(outputItem, out var item))
            {
                continue;
            }

            var type = GetString(item, "type");
            if (type == "message")
            {
                foreach (var blockItem in ListValue(item, "content"))
                {
                    if (!TryAsObject(blockItem, out var block))
                    {
                        continue;
                    }

                    if (GetString(block, "type") is "output_text" or "text")
                    {
                        textParts.Add(Convert.ToString(GetValue(block, "text")) ?? string.Empty);
                        annotations.AddRange(NormalizeAnnotations(GetValue(block, "annotations")));
                    }
                    else if (GetString(block, "type") == "refusal")
                    {
                        refusalParts.Add(StringifyContent(GetValue(block, "refusal") ?? string.Empty));
                    }
                }
            }
            else if (type == "reasoning")
            {
                var reasoning = ResponsesReasoningToText(item);
                if (!string.IsNullOrEmpty(reasoning))
                {
                    reasoningParts.Add(reasoning);
                }
            }
            else if (type == "mcp_call")
            {
                var callId = GetValue(item, "id") ?? NewId("mcp");
                toolCalls.Add(Obj(
                    ("id", callId),
                    ("name", GetValue(item, "name")),
                    ("arguments", GetValue(item, "arguments") ?? "{}"),
                    ("native_type", "mcp"),
                    ("server_name", GetValue(item, "server_label"))));
                if (HasNonNullValue(item, "output") || HasNonNullValue(item, "error"))
                {
                    toolResults.Add(Obj(
                        ("id", callId),
                        ("output", GetValue(item, "output") ?? GetValue(item, "error") ?? string.Empty),
                        ("is_error", HasNonNullValue(item, "error")),
                        ("native_type", "mcp")));
                }
            }
            else if (IsServerExecutedToolSearchResponseItem(item))
            {
                continue;
            }
            else if (IsResponsesToolCallLike(item))
            {
                var arguments = ResponsesToolCallArguments(item);
                if (GetResponsesToolCallKind(ResponsesToolCallName(item)) == ResponsesToolCallKind.CustomTool)
                {
                    arguments = NormalizeApplyPatchArguments(type ?? string.Empty, ResponsesToolCallName(item), arguments);
                }

                toolCalls.Add(Obj(
                    ("id", GetValue(item, "call_id") ?? GetValue(item, "id") ?? NewId("call")),
                    ("name", ResponsesToolCallName(item)),
                    ("namespace", GetValue(item, "namespace")),
                    ("arguments", JsonDumps(arguments))));
            }
        }

        return Obj(
            ("id", GetValue(payload, "id") ?? NewId("resp")),
            ("model", originalModel ?? GetValue(payload, "model")),
            ("created", GetValue(payload, "created_at") ?? Now()),
            ("text", string.Concat(textParts)),
            ("reasoning", string.Concat(reasoningParts)),
            ("refusal", string.Concat(refusalParts)),
            ("annotations", annotations),
            ("tool_calls", toolCalls),
            ("tool_results", toolResults),
            ("finish_reason", ResponsesStatusToCanonicalFinishReason(payload, toolCalls.Count > 0)),
            ("usage", ResponsesUsageToCanonical(ObjectValue(payload, "usage"))),
            ("raw", DeepCopy(payload)));
    }

    private static Dictionary<string, object?> ChatResponseToCanonical(
        Dictionary<string, object?> payload,
        string? originalModel,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? toolCallMappings)
    {
        var choice = FirstObject(ListValue(payload, "choices")) ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        var message = ObjectValue(choice, "message");
        var toolCalls = new List<object?>();

        foreach (var toolCallItem in ListValue(message, "tool_calls"))
        {
            if (!TryAsObject(toolCallItem, out var toolCall))
            {
                continue;
            }

            var callType = GetString(toolCall, "type") ?? "function";
            var callPayload = callType == "custom" ? ObjectValue(toolCall, "custom") : ObjectValue(toolCall, "function");
            var toolName = GetString(callPayload, "name");
            var shape = ResolveResponsesToolCallShape(toolName, toolCallMappings);
            var responseName = string.IsNullOrEmpty(shape.Name)
                ? toolName
                : shape.Name;
            var (namespaceName, _) = NamespaceCallParts(responseName, shape.Namespace);
            var canonicalToolCall = Obj(
                ("id", GetValue(toolCall, "id") ?? NewId("call")),
                ("name", responseName),
                ("namespace", namespaceName),
                ("arguments", callType == "custom"
                    ? GetValue(callPayload, "input") ?? string.Empty
                    : GetValue(callPayload, "arguments") ?? "{}"));
            if (callType == "custom")
            {
                canonicalToolCall["native_type"] = "custom";
            }
            if (shape.Kind != ResponsesToolCallKind.Function)
            {
                canonicalToolCall["native_type"] = shape.Kind == ResponsesToolCallKind.CustomTool
                    ? "custom"
                    : shape.ItemType == "custom_tool_call"
                        ? "custom"
                        : shape.ItemType.EndsWith("_call", StringComparison.Ordinal)
                        ? shape.ItemType[..^"_call".Length]
                        : shape.ItemType;
            }

            toolCalls.Add(canonicalToolCall);
        }

        return Obj(
            ("id", GetValue(payload, "id") ?? NewId("chatcmpl")),
            ("model", originalModel ?? GetValue(payload, "model")),
            ("created", GetValue(payload, "created") ?? Now()),
            ("text", StringifyContent(GetValue(message, "content") ?? string.Empty)),
            ("reasoning", StringifyContent(GetValue(message, "reasoning_content") ?? string.Empty)),
            ("refusal", StringifyContent(GetValue(message, "refusal") ?? string.Empty)),
            ("annotations", NormalizeAnnotations(GetValue(message, "annotations"))),
            ("tool_calls", toolCalls),
            ("finish_reason", ChatFinishReasonToCanonical(GetValue(choice, "finish_reason"))),
            ("usage", ChatUsageToCanonical(ObjectValue(payload, "usage"))),
            ("raw", DeepCopy(payload)));
    }

    private static Dictionary<string, object?> MessagesResponseToCanonical(
        Dictionary<string, object?> payload,
        string? originalModel,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? toolCallMappings = null)
    {
        var textParts = new List<string>();
        var reasoningParts = new List<string>();
        var toolCalls = new List<object?>();
        var thinkingBlocks = new List<object?>();
        var toolResults = new List<object?>();
        foreach (var contentItem in ListValue(payload, "content"))
        {
            if (!TryAsObject(contentItem, out var block))
            {
                continue;
            }

            var blockType = GetString(block, "type");
            if (blockType == "thinking")
            {
                var thinking = StringifyContent(GetValue(block, "thinking") ?? string.Empty);
                if (!string.IsNullOrEmpty(thinking))
                {
                    reasoningParts.Add(thinking);
                }

                var cleaned = new Dictionary<string, object?>(StringComparer.Ordinal);
                cleaned["type"] = "thinking";
                cleaned["thinking"] = thinking;
                if (block.TryGetValue("signature", out var sig)) cleaned["signature"] = sig;
                thinkingBlocks.Add(cleaned);
            }
            else if (blockType == "redacted_thinking")
            {
                var cleaned = new Dictionary<string, object?>(StringComparer.Ordinal);
                cleaned["type"] = "redacted_thinking";
                if (block.TryGetValue("data", out var data)) cleaned["data"] = data;
                if (block.TryGetValue("signature", out var sig)) cleaned["signature"] = sig;
                thinkingBlocks.Add(cleaned);
            }
            else if (blockType == "text")
            {
                textParts.Add(Convert.ToString(GetValue(block, "text")) ?? string.Empty);
            }
            else if (blockType is "tool_use" or "mcp_tool_use")
            {
                var toolName = GetValue(block, "name");
                var call = Obj(
                    ("id", GetValue(block, "id") ?? NewId("call")),
                    ("name", toolName),
                    ("arguments", JsonDumps(GetValue(block, "input") ?? new Dictionary<string, object?>())));
                if (blockType == "mcp_tool_use")
                {
                    call["native_type"] = "mcp";
                    call["server_name"] = GetValue(block, "server_name");
                }
                else
                {
                    var shape = ResolveResponsesToolCallShape(toolName, toolCallMappings);
                    if (shape.Kind != ResponsesToolCallKind.Function)
                    {
                        call["native_type"] = shape.Kind == ResponsesToolCallKind.CustomTool
                            ? "custom"
                            : shape.ItemType == "custom_tool_call"
                                ? "custom"
                                : shape.ItemType.EndsWith("_call", StringComparison.Ordinal)
                                    ? shape.ItemType[..^"_call".Length]
                                    : shape.ItemType;
                        if (!string.IsNullOrEmpty(shape.Namespace))
                        {
                            call["namespace"] = shape.Namespace;
                        }
                    }
                }

                toolCalls.Add(call);
            }
            else if (blockType == "mcp_tool_result")
            {
                toolResults.Add(Obj(
                    ("id", GetValue(block, "tool_use_id")),
                    ("output", StringifyContent(GetValue(block, "content") ?? string.Empty)),
                    ("is_error", GetValue(block, "is_error") ?? false),
                    ("native_type", "mcp")));
            }
        }

        var reasoning = string.Concat(reasoningParts);
        var encodedThinking = thinkingBlocks.Count > 0 && thinkingBlocks.Any(b => (b as Dictionary<string, object?>)?.ContainsKey("signature") == true)
            ? EncodeAnthropicThinkingBlocks(thinkingBlocks)
            : null;

        return Obj(
            ("id", GetValue(payload, "id") ?? NewId("msg")),
            ("model", originalModel ?? GetValue(payload, "model")),
            ("created", Now()),
            ("text", string.Concat(textParts)),
            ("reasoning", reasoning),
            ("anthropic_thinking_encrypted", encodedThinking),
            ("tool_calls", toolCalls),
            ("tool_results", toolResults),
            ("finish_reason", MessagesStopReasonToCanonical(GetValue(payload, "stop_reason"))),
            ("usage", MessagesUsageToCanonical(ObjectValue(payload, "usage"))),
            ("raw", DeepCopy(payload)));
    }

    private static Dictionary<string, object?> CanonicalToResponsesResponse(Dictionary<string, object?> canonical)
    {
        var output = new List<object?>();
        var reasoning = StringifyContent(GetValue(canonical, "reasoning") ?? string.Empty);
        if (!string.IsNullOrEmpty(reasoning))
        {
            // Use encoded thinking blocks as encrypted_content when available (preserves Anthropic signatures)
            var thinkingEncrypted = GetValue(canonical, "anthropic_thinking_encrypted") as string;
            var encryptedContent = !string.IsNullOrEmpty(thinkingEncrypted)
                ? thinkingEncrypted
                : reasoning;

            output.Add(Obj(
                ("id", NewId("rs")),
                ("type", "reasoning"),
                ("status", "completed"),
                ("summary", new List<object?> { Obj(("type", "summary_text"), ("text", reasoning)) }),
                ("encrypted_content", encryptedContent)));
        }

        var text = StringifyContent(GetValue(canonical, "text") ?? string.Empty);
        if (!string.IsNullOrEmpty(text))
        {
            var outputText = Obj(("type", "output_text"), ("text", text));
            var annotations = ListValue(canonical, "annotations");
            if (annotations.Count > 0)
            {
                outputText["annotations"] = DeepCopy(annotations);
            }

            output.Add(Obj(
                ("id", NewId("msg")),
                ("type", "message"),
                ("status", "completed"),
                ("role", "assistant"),
                ("content", new List<object?> { outputText })));
        }

        var refusal = StringifyContent(GetValue(canonical, "refusal") ?? string.Empty);
        if (!string.IsNullOrEmpty(refusal))
        {
            output.Add(Obj(
                ("id", NewId("msg")),
                ("type", "message"),
                ("status", "completed"),
                ("role", "assistant"),
                ("content", new List<object?> { Obj(("type", "refusal"), ("refusal", refusal)) })));
        }

        foreach (var toolCallItem in ListValue(canonical, "tool_calls"))
        {
            if (!TryAsObject(toolCallItem, out var toolCall))
            {
                continue;
            }

            var nativeType = GetString(toolCall, "native_type") ?? string.Empty;
            if (nativeType == "mcp")
            {
                var callId = GetValue(toolCall, "id") ?? NewId("mcp");
                var resultItem = ListValue(canonical, "tool_results")
                    .FirstOrDefault(item => TryAsObject(item, out var result) && Equals(GetValue(result, "id"), callId));
                var mcp = Obj(
                    ("id", callId),
                    ("type", "mcp_call"),
                    ("name", GetValue(toolCall, "name")),
                    ("arguments", GetValue(toolCall, "arguments") ?? "{}"),
                    ("server_label", GetValue(toolCall, "server_name") ?? string.Empty),
                    ("status", "completed"));
                if (TryAsObject(resultItem, out var toolResult))
                {
                    if (IsTruthy(GetValue(toolResult, "is_error")))
                    {
                        mcp["error"] = GetValue(toolResult, "output");
                    }
                    else
                    {
                        mcp["output"] = GetValue(toolResult, "output");
                    }
                }

                output.Add(mcp);
                continue;
            }

            output.Add(ResponsesToolCallItemFromToolCall(
                GetValue(toolCall, "id"),
                GetValue(toolCall, "name"),
                GetValue(toolCall, "arguments") ?? "{}",
                GetValue(toolCall, "namespace"),
                mappings: CanonicalToolCallMappings(toolCall)));
        }

        var finishReason = GetString(canonical, "finish_reason") ?? "stop";
        var incomplete = finishReason is "length" or "content_filter";
        var response = Obj(
            ("id", GetValue(canonical, "id") ?? NewId("resp")),
            ("object", "response"),
            ("created_at", GetValue(canonical, "created") ?? Now()),
            ("status", incomplete ? "incomplete" : "completed"),
            ("model", GetValue(canonical, "model")),
            ("output", output),
            ("usage", CanonicalUsageToResponses(ObjectValue(canonical, "usage"))));
        if (incomplete)
        {
            response["incomplete_details"] = Obj(("reason", finishReason == "content_filter" ? "content_filter" : "max_output_tokens"));
        }

        return response;
    }

    private static Dictionary<string, object?> CanonicalToChatResponse(Dictionary<string, object?> canonical)
    {
        var message = Obj(
            ("role", "assistant"),
            ("content", IsTruthy(GetValue(canonical, "text")) ? GetValue(canonical, "text") : null));

        var chatReasoning = StringifyContent(GetValue(canonical, "reasoning") ?? string.Empty);
        if (!string.IsNullOrEmpty(chatReasoning))
        {
            message["reasoning_content"] = chatReasoning;
        }

        var thinkingEncrypted = GetValue(canonical, "anthropic_thinking_encrypted") as string;
        if (!string.IsNullOrEmpty(thinkingEncrypted))
        {
            message["anthropic_thinking_encrypted"] = thinkingEncrypted;
        }

        var canonicalToolCalls = ListValue(canonical, "tool_calls");
        if (canonicalToolCalls.Count > 0)
        {
            if (canonicalToolCalls.Any(item => TryAsObject(item, out var call) && GetString(call, "native_type") == "mcp"))
            {
                throw new BadRequestException("native MCP responses cannot be represented by Chat Completions; use Responses or Messages protocol");
            }

            message["tool_calls"] = canonicalToolCalls
                .Where(item => TryAsObject(item, out _))
                .Select(item =>
                {
                    var toolCall = AsObject(item);
                    var namespaceName = GetString(toolCall, "namespace");
                    var name = Convert.ToString(GetValue(toolCall, "name")) ?? string.Empty;
                    if (!string.IsNullOrEmpty(namespaceName))
                    {
                        name = $"{namespaceName}{NamespaceSeparator}{name}";
                    }

                    return (object?)Obj(
                        ("id", GetValue(toolCall, "id")),
                        ("type", "function"),
                        ("function", Obj(
                            ("name", NamespaceNameToChat(name)),
                            ("arguments", GetValue(toolCall, "arguments") ?? "{}"))));
                })
                .ToList();
        }

        var chatAnnotations = CanonicalAnnotationsToChat(ListValue(canonical, "annotations"));
        if (chatAnnotations.Count > 0)
        {
            message["annotations"] = chatAnnotations;
        }

        var chatRefusal = StringifyContent(GetValue(canonical, "refusal") ?? string.Empty);
        if (!string.IsNullOrEmpty(chatRefusal))
        {
            message["refusal"] = chatRefusal;
        }

        return Obj(
            ("id", GetValue(canonical, "id") ?? NewId("chatcmpl")),
            ("object", "chat.completion"),
            ("created", GetValue(canonical, "created") ?? Now()),
            ("model", GetValue(canonical, "model")),
            ("choices", new List<object?>
            {
                Obj(
                    ("index", 0),
                    ("message", message),
                    ("finish_reason", GetValue(canonical, "finish_reason") ?? "stop"))
            }),
            ("usage", CanonicalUsageToChat(ObjectValue(canonical, "usage"))));
    }

    private static IReadOnlyDictionary<string, ResponsesToolCallMapping>? CanonicalToolCallMappings(
        Dictionary<string, object?> toolCall)
    {
        var nativeType = GetString(toolCall, "native_type");
        var responsesName = Convert.ToString(GetValue(toolCall, "name")) ?? string.Empty;
        if (string.IsNullOrEmpty(nativeType) || string.IsNullOrEmpty(responsesName))
        {
            return null;
        }

        var chatName = NamespaceNameToChat(responsesName);
        return new Dictionary<string, ResponsesToolCallMapping>(StringComparer.Ordinal)
        {
            [chatName] = new ResponsesToolCallMapping
            {
                ChatName = chatName,
                NativeType = nativeType,
                ResponsesName = responsesName,
                Namespace = GetString(toolCall, "namespace")
            }
        };
    }

    private static Dictionary<string, object?> CanonicalToMessagesResponse(Dictionary<string, object?> canonical)
    {
        var content = new List<object?>();
        var text = StringifyContent(GetValue(canonical, "text") ?? string.Empty);
        if (!string.IsNullOrEmpty(text))
        {
            content.Add(Obj(("type", "text"), ("text", text)));
        }

        foreach (var toolCallItem in ListValue(canonical, "tool_calls"))
        {
            if (!TryAsObject(toolCallItem, out var toolCall))
            {
                continue;
            }

            var nativeType = GetString(toolCall, "native_type") ?? string.Empty;
            if (nativeType == "mcp")
            {
                var callId = GetValue(toolCall, "id");
                content.Add(Obj(
                    ("type", "mcp_tool_use"),
                    ("id", callId),
                    ("name", GetValue(toolCall, "name")),
                    ("server_name", GetValue(toolCall, "server_name") ?? string.Empty),
                    ("input", ParseJsonObject(GetValue(toolCall, "arguments") ?? "{}"))));
                var resultItem = ListValue(canonical, "tool_results")
                    .FirstOrDefault(item => TryAsObject(item, out var result) && Equals(GetValue(result, "id"), callId));
                if (TryAsObject(resultItem, out var mcpResult))
                {
                    content.Add(Obj(
                        ("type", "mcp_tool_result"),
                        ("tool_use_id", callId),
                        ("is_error", GetValue(mcpResult, "is_error") ?? false),
                        ("content", new List<object?>
                        {
                            Obj(("type", "text"), ("text", StringifyContent(GetValue(mcpResult, "output") ?? string.Empty)))
                        })));
                }

                continue;
            }

            var namespaceName = GetString(toolCall, "namespace");
            var name = Convert.ToString(GetValue(toolCall, "name")) ?? string.Empty;
            if (!string.IsNullOrEmpty(namespaceName))
            {
                name = $"{namespaceName}{NamespaceSeparator}{name}";
            }

            content.Add(Obj(
                ("type", "tool_use"),
                ("id", GetValue(toolCall, "id")),
                ("name", name),
                ("input", ParseJsonObject(GetValue(toolCall, "arguments") ?? "{}"))));
        }

        return Obj(
            ("id", GetValue(canonical, "id") ?? NewId("msg")),
            ("type", "message"),
            ("role", "assistant"),
            ("model", GetValue(canonical, "model")),
            ("content", content),
            ("stop_reason", CanonicalFinishReasonToMessages(GetString(canonical, "finish_reason") ?? "stop")),
            ("stop_sequence", null),
            ("usage", CanonicalUsageToMessages(ObjectValue(canonical, "usage"))));
    }

    private static bool IsServerExecutedToolSearchResponseItem(Dictionary<string, object?> item)
    {
        return string.Equals(GetString(item, "type"), "tool_search_call", StringComparison.Ordinal)
            && string.Equals(GetString(item, "execution"), "server", StringComparison.Ordinal);
    }
}

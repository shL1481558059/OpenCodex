using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static Dictionary<string, object?> ToCanonicalRequest(Dictionary<string, object?> payload, string protocol, IReadOnlyDictionary<string, object?>? compat)
    {
        return protocol switch
        {
            Responses => ResponsesRequestToCanonical(payload, compat),
            Chat => ChatRequestToCanonical(payload, compat),
            Messages => MessagesRequestToCanonical(payload, compat),
            _ => throw new BadRequestException($"unsupported source protocol: {protocol}")
        };
    }

    private static Dictionary<string, object?> FromCanonicalRequest(Dictionary<string, object?> canonical, string protocol)
    {
        return protocol switch
        {
            Responses => CanonicalToResponsesRequest(canonical),
            Chat => CanonicalToChatRequest(canonical),
            Messages => CanonicalToMessagesRequest(canonical),
            _ => throw new BadRequestException($"unsupported target protocol: {protocol}")
        };
    }

    private static Dictionary<string, object?> ResponsesRequestToCanonical(Dictionary<string, object?> payload, IReadOnlyDictionary<string, object?>? compat)
    {
        var messages = new List<object?>();
        var instructions = GetValue(payload, "instructions");
        var hasPlanModeTag = ResponsesPayloadHasPlanModeTag(payload);
        if (IsTruthy(instructions))
        {
            messages.Add(Obj(("role", "system"), ("content", StringifyContent(instructions))));
        }

        var rawInput = GetValue(payload, "input") ?? new List<object?>();
        if (rawInput is string inputText)
        {
            messages.Add(Obj(("role", "user"), ("content", inputText)));
        }
        else if (TryAsList(rawInput, out var inputItems))
        {
            foreach (var item in inputItems)
            {
                messages.AddRange(ResponsesInputItemToMessages(item));
            }
        }
        else
        {
            throw new BadRequestException("responses input must be a string or list");
        }

        messages = NormalizeChatToolHistory(messages);
        messages = MergeSystemMessages(messages);
        if (hasPlanModeTag)
        {
            messages = AppendSystemInstruction(messages, PlanModeTagInstruction);
        }

        return Obj(
            ("model", GetValue(payload, "model")),
            ("messages", messages),
            ("tools", ResponsesRequestToolsToCanonical(payload, compat)),
            ("tool_choice", GetValue(payload, "tool_choice")),
            ("params", CopyCommonRequestParams(payload, Responses)));
    }

    private static Dictionary<string, object?> ChatRequestToCanonical(Dictionary<string, object?> payload, IReadOnlyDictionary<string, object?>? compat)
    {
        var messages = new List<object?>();
        foreach (var item in ListValue(payload, "messages"))
        {
            if (TryAsObject(item, out var message))
            {
                messages.Add(DeepCopy(message));
            }
        }

        return Obj(
            ("model", GetValue(payload, "model")),
            ("messages", messages),
            ("tools", ChatToolsToCanonical(GetValue(payload, "tools"), compat)),
            ("tool_choice", GetValue(payload, "tool_choice")),
            ("params", CopyCommonRequestParams(payload, Chat)));
    }

    private static Dictionary<string, object?> MessagesRequestToCanonical(Dictionary<string, object?> payload, IReadOnlyDictionary<string, object?>? compat)
    {
        var messages = new List<object?>();
        var system = GetValue(payload, "system");
        if (IsTruthy(system))
        {
            messages.Add(Obj(("role", "system"), ("content", StringifyContent(system))));
        }

        foreach (var item in ListValue(payload, "messages"))
        {
            if (!TryAsObject(item, out var message))
            {
                continue;
            }

            messages.AddRange(AnthropicMessageToCanonicalMessages(message));
        }

        return Obj(
            ("model", GetValue(payload, "model")),
            ("messages", messages),
            ("tools", AnthropicToolsToCanonical(GetValue(payload, "tools"), GetValue(payload, "mcp_servers"))),
            ("tool_choice", GetValue(payload, "tool_choice")),
            ("params", CopyCommonRequestParams(payload, Messages)));
    }

    private static Dictionary<string, object?> CanonicalToResponsesRequest(Dictionary<string, object?> canonical)
    {
        var result = Obj(("model", GetValue(canonical, "model")));
        MergeInto(result, ObjectValue(canonical, "params"));

        if (HasNonNullValue(result, "reasoning_effort") && !HasNonNullValue(result, "reasoning"))
        {
            result["reasoning"] = Obj(("effort", GetValue(result, "reasoning_effort")));
        }

        if (TryAsObject(GetValue(result, "response_format"), out var responseFormat) && !HasNonNullValue(result, "text"))
        {
            result["text"] = Obj(("format", ChatResponseFormatToResponsesFormat(responseFormat)));
        }

        if (TryAsObject(GetValue(result, "output_config"), out var outputConfig)
            && TryAsObject(GetValue(outputConfig, "format"), out var outputFormat)
            && !HasNonNullValue(result, "text"))
        {
            result["text"] = Obj(("format", DeepCopy(outputFormat)));
        }

        var (instructions, input) = MessagesToResponsesInput(ListValue(canonical, "messages"));
        if (!string.IsNullOrEmpty(instructions))
        {
            result["instructions"] = instructions;
        }

        result["input"] = input;

        var tools = CanonicalToolsToResponses(ListValue(canonical, "tools"));
        if (tools.Count > 0)
        {
            result["tools"] = tools;
        }

        if (HasNonNullValue(canonical, "tool_choice"))
        {
            result["tool_choice"] = ToolChoiceToResponses(GetValue(canonical, "tool_choice"));
        }

        if (result.ContainsKey("max_tokens") && !result.ContainsKey("max_output_tokens"))
        {
            result["max_output_tokens"] = result["max_tokens"];
            result.Remove("max_tokens");
        }

        FilterRequestParameters(result, Responses);

        return result;
    }

    private static Dictionary<string, object?> CanonicalToChatRequest(Dictionary<string, object?> canonical)
    {
        var result = Obj(("model", GetValue(canonical, "model")), ("messages", new List<object?>()));
        MergeInto(result, ObjectValue(canonical, "params"));

        if (TryAsObject(GetValue(result, "reasoning"), out var reasoning)
            && HasNonNullValue(reasoning, "effort")
            && !HasNonNullValue(result, "reasoning_effort"))
        {
            result["reasoning_effort"] = GetValue(reasoning, "effort");
        }

        if (TryAsObject(GetValue(result, "text"), out var textConfig)
            && TryAsObject(GetValue(textConfig, "format"), out var responsesFormat)
            && !HasNonNullValue(result, "response_format"))
        {
            result["response_format"] = ResponsesFormatToChatResponseFormat(responsesFormat);
        }

        if (TryAsObject(GetValue(result, "output_config"), out var outputConfig)
            && TryAsObject(GetValue(outputConfig, "format"), out var outputFormat)
            && !HasNonNullValue(result, "response_format"))
        {
            result["response_format"] = ResponsesFormatToChatResponseFormat(outputFormat);
        }

        var outputMessages = ListValue(result, "messages");
        var preserveThinkingHistory = IsTruthy(GetValue(result, "_ocxp_preserve_thinking_history"));
        result.Remove("_ocxp_preserve_thinking_history");

        foreach (var item in ListValue(canonical, "messages"))
        {
            if (!TryAsObject(item, out var message))
            {
                continue;
            }

            var role = GetString(message, "role") ?? "user";
            if (ListValue(message, "tool_calls").Any(item => TryAsObject(item, out var call) && GetString(call, "native_type") == "mcp")
                || (role == "tool" && GetString(message, "native_type") == "mcp"))
            {
                throw new BadRequestException("native MCP history cannot be represented by Chat Completions; use Responses or Messages protocol");
            }

            var converted = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in new[] { "role", "content", "tool_calls", "tool_call_id", "name", "reasoning_content", "anthropic_thinking_encrypted" }
                     .Where(key => preserveThinkingHistory || key is not "reasoning_content" and not "anthropic_thinking_encrypted"))
            {
                if (message.TryGetValue(key, out var value))
                {
                    converted[key] = DeepCopy(value);
                }
            }

            if (GetString(converted, "role") == "developer")
            {
                converted["role"] = "system";
            }

            outputMessages.Add(converted);
        }

        var tools = CanonicalToolsToChat(ListValue(canonical, "tools"));
        if (tools.Count > 0)
        {
            result["tools"] = tools;
        }

        if (HasNonNullValue(canonical, "tool_choice"))
        {
            result["tool_choice"] = ToolChoiceToChat(GetValue(canonical, "tool_choice"));
        }

        if (result.ContainsKey("max_output_tokens") && !result.ContainsKey("max_tokens"))
        {
            result["max_tokens"] = result["max_output_tokens"];
            result.Remove("max_output_tokens");
        }

        if (result.TryGetValue("stop_sequences", out var stopSequences) && !result.ContainsKey("stop"))
        {
            result["stop"] = stopSequences;
        }

        FilterRequestParameters(result, Chat);

        return result;
    }

    private static Dictionary<string, object?> CanonicalToMessagesRequest(Dictionary<string, object?> canonical)
    {
        var result = Obj(("model", GetValue(canonical, "model")), ("messages", new List<object?>()));
        MergeInto(result, ObjectValue(canonical, "params"));

        if (TryAsObject(GetValue(result, "text"), out var textConfig)
            && TryAsObject(GetValue(textConfig, "format"), out var format)
            && !HasNonNullValue(result, "output_config"))
        {
            result["output_config"] = Obj(("format", DeepCopy(format)));
        }

        if (TryAsObject(GetValue(result, "response_format"), out var responseFormat)
            && !HasNonNullValue(result, "output_config"))
        {
            result["output_config"] = Obj(("format", ChatResponseFormatToResponsesFormat(responseFormat)));
        }

        DropResponsesOnlyParamsForMessages(result);

        // Read internal marker injected by ChannelCompatRequestRewriter
        var preserveThinkingHistory = IsTruthy(GetValue(result, "_ocxp_preserve_thinking_history"));
        result.Remove("_ocxp_preserve_thinking_history");

        // If the upstream already has a thinking param, respect it
        var alreadyHasThinking = TryAsObject(GetValue(result, "thinking"), out _);
        var injectedThinkingBlocks = false;

        var systemParts = new List<string>();
        var outputMessages = ListValue(result, "messages");
        foreach (var item in ListValue(canonical, "messages"))
        {
            if (!TryAsObject(item, out var message))
            {
                continue;
            }

            var role = GetString(message, "role") ?? "user";
            if (role is "system" or "developer")
            {
                var text = StringifyContent(GetValue(message, "content"));
                if (!string.IsNullOrEmpty(text))
                {
                    systemParts.Add(text);
                }

                continue;
            }

            if (role == "tool")
            {
                var nativeMcp = GetString(message, "native_type") == "mcp";
                outputMessages.Add(Obj(
                    ("role", "user"),
                    ("content", new List<object?>
                    {
                        Obj(
                            ("type", nativeMcp ? "mcp_tool_result" : "tool_result"),
                            ("tool_use_id", GetValue(message, "tool_call_id")),
                            ("is_error", nativeMcp ? GetValue(message, "is_error") ?? false : null),
                            ("content", StringifyContent(GetValue(message, "content") ?? string.Empty)))
                    })));
                continue;
            }

            if (role == "assistant" && preserveThinkingHistory)
            {
                var anthropicThinkingEncrypted = GetString(message, "anthropic_thinking_encrypted") ?? string.Empty;
                if (!string.IsNullOrEmpty(anthropicThinkingEncrypted)
                    && TryDecodeAnthropicThinkingBlocks(anthropicThinkingEncrypted, out var decodedBlocks)
                    && decodedBlocks.Count > 0)
                {
                    var content = CanonicalMessageToAnthropicContent(message);
                    content.InsertRange(0, decodedBlocks);
                    outputMessages.Add(Obj(("role", role), ("content", content)));
                    injectedThinkingBlocks = true;
                    continue;
                }

                // 没有可用签名时（如 Responses 入口只带 reasoning summary）降级为文本块，
                // 避免整条推理历史被丢弃、只剩下空 content 的 assistant 消息。
                var reasoningText = StringifyContent(GetValue(message, "reasoning_content") ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(reasoningText))
                {
                    var content = CanonicalMessageToAnthropicContent(message);
                    content.Insert(0, Obj(
                        ("type", "text"),
                        ("text", $"{ReasoningTextOpenTag}\n{reasoningText}\n{ReasoningTextCloseTag}")));
                    outputMessages.Add(Obj(("role", role), ("content", content)));
                    continue;
                }
            }

            outputMessages.Add(Obj(
                ("role", role),
                ("content", CanonicalMessageToAnthropicContent(message))));
        }

        if (systemParts.Count > 0)
        {
            result["system"] = string.Join("\n\n", systemParts);
        }

        var tools = CanonicalToolsToAnthropic(ListValue(canonical, "tools"));
        if (tools.Count > 0)
        {
            result["tools"] = tools;
        }

        var mcpServers = BuildAnthropicMcpServers(ListValue(canonical, "tools"));
        if (mcpServers.Count > 0)
        {
            result["mcp_servers"] = mcpServers;
        }

        if (HasNonNullValue(canonical, "tool_choice"))
        {
            result["tool_choice"] = ToolChoiceToMessages(GetValue(canonical, "tool_choice"));
        }

        if (result.ContainsKey("max_output_tokens") && !result.ContainsKey("max_tokens"))
        {
            result["max_tokens"] = result["max_output_tokens"];
            result.Remove("max_output_tokens");
        }

        if (result.TryGetValue("stop", out var stop) && !result.ContainsKey("stop_sequences"))
        {
            result["stop_sequences"] = stop is string stopText
                ? new List<object?> { stopText }
                : DeepCopy(stop);
        }

        // Auto-inject thinking param when preserve_thinking_history is enabled
        // and we injected thinking blocks into assistant messages
        if (preserveThinkingHistory && !alreadyHasThinking && injectedThinkingBlocks)
        {
            var budgetTokens = ToInt(GetValue(result, "_ocxp_thinking_budget_tokens"));
            result.Remove("_ocxp_thinking_budget_tokens");
            if (budgetTokens <= 0)
            {
                budgetTokens = 10000;
            }

            result["thinking"] = Obj(("type", "enabled"), ("budget_tokens", budgetTokens));
        }

        FilterRequestParameters(result, Messages);
        if (!HasNonNullValue(result, "max_tokens"))
        {
            result["max_tokens"] = 4096;
        }

        return result;
    }

    private static List<object?> AnthropicMessageToCanonicalMessages(Dictionary<string, object?> message)
    {
        var role = GetString(message, "role") ?? "user";
        var rawContent = GetValue(message, "content") ?? string.Empty;
        if (!TryAsList(rawContent, out var blocks))
        {
            return [Obj(("role", role), ("content", AnthropicContentToChatContent(rawContent)))];
        }

        if (role == "assistant")
        {
            var normalBlocks = new List<object?>();
            var toolCalls = new List<object?>();
            var reasoningParts = new List<string>();
            var thinkingBlocks = new List<object?>();
            foreach (var blockItem in blocks)
            {
                if (!TryAsObject(blockItem, out var block))
                {
                    continue;
                }

                var type = GetString(block, "type") ?? string.Empty;
                if (type is "tool_use" or "mcp_tool_use")
                {
                    var toolCall = Obj(
                        ("id", GetValue(block, "id") ?? NewId("call")),
                        ("type", "function"),
                        ("function", Obj(
                            ("name", GetValue(block, "name") ?? string.Empty),
                            ("arguments", JsonDumps(GetValue(block, "input") ?? new Dictionary<string, object?>())))));
                    if (type == "mcp_tool_use")
                    {
                        toolCall["native_type"] = "mcp";
                        toolCall["server_name"] = GetValue(block, "server_name");
                    }

                    toolCalls.Add(toolCall);
                    continue;
                }

                if (type == "thinking")
                {
                    reasoningParts.Add(StringifyContent(GetValue(block, "thinking") ?? string.Empty));
                    thinkingBlocks.Add(DeepCopy(block));
                    continue;
                }

                if (type == "redacted_thinking")
                {
                    thinkingBlocks.Add(DeepCopy(block));
                    continue;
                }

                normalBlocks.Add(DeepCopy(block));
            }

            var canonical = Obj(
                ("role", "assistant"),
                ("content", AnthropicContentToChatContent(normalBlocks)));
            if (toolCalls.Count > 0)
            {
                canonical["tool_calls"] = toolCalls;
            }

            var reasoning = string.Concat(reasoningParts);
            if (!string.IsNullOrEmpty(reasoning))
            {
                canonical["reasoning_content"] = reasoning;
            }

            if (thinkingBlocks.Any(item => TryAsObject(item, out var block) && HasNonNullValue(block, "signature")))
            {
                canonical["anthropic_thinking_encrypted"] = EncodeAnthropicThinkingBlocks(thinkingBlocks);
            }

            return [canonical];
        }

        var result = new List<object?>();
        var pendingBlocks = new List<object?>();
        void FlushPending()
        {
            if (pendingBlocks.Count == 0)
            {
                return;
            }

            var content = AnthropicContentToChatContent(pendingBlocks);
            if (!IsEmptyChatContent(content))
            {
                result.Add(Obj(("role", role), ("content", content)));
            }

            pendingBlocks.Clear();
        }

        foreach (var blockItem in blocks)
        {
            if (!TryAsObject(blockItem, out var block))
            {
                continue;
            }

            var type = GetString(block, "type") ?? string.Empty;
            if (type is not ("tool_result" or "mcp_tool_result"))
            {
                pendingBlocks.Add(DeepCopy(block));
                continue;
            }

            FlushPending();
            var toolMessage = Obj(
                ("role", "tool"),
                ("tool_call_id", GetValue(block, "tool_use_id")),
                ("content", AnthropicContentToChatContent(GetValue(block, "content") ?? string.Empty)));
            if (HasNonNullValue(block, "is_error"))
            {
                toolMessage["is_error"] = GetValue(block, "is_error");
            }

            if (type == "mcp_tool_result")
            {
                toolMessage["native_type"] = "mcp";
            }

            result.Add(toolMessage);
        }

        FlushPending();
        return result;
    }


    private static List<object?> CanonicalMessageToAnthropicContent(Dictionary<string, object?> message)
    {
        var content = ChatContentToAnthropicContent(GetValue(message, "content") ?? string.Empty);
        foreach (var toolCallItem in ListValue(message, "tool_calls"))
        {
            if (!TryAsObject(toolCallItem, out var toolCall))
            {
                continue;
            }

            var function = ObjectValue(toolCall, "function");
            var nativeMcp = GetString(toolCall, "native_type") == "mcp";
            content.Add(Obj(
                ("type", nativeMcp ? "mcp_tool_use" : "tool_use"),
                ("id", GetValue(toolCall, "id")),
                ("name", GetValue(function, "name") ?? GetValue(toolCall, "name")),
                ("server_name", nativeMcp ? GetValue(toolCall, "server_name") ?? string.Empty : null),
                ("input", ParseJsonObject(GetValue(function, "arguments") ?? GetValue(toolCall, "arguments") ?? "{}"))));
        }

        return content;
    }

    private static void DropResponsesOnlyParamsForMessages(Dictionary<string, object?> payload)
    {
        foreach (var key in new[]
                 {
                     "include",
                     "reasoning",
                     "text",
                     "previous_response_id",
                     "client_metadata",
                     "parallel_tool_calls",
                     "prompt_cache_key",
                     "store"
                 })
        {
            payload.Remove(key);
        }
    }

    private static Dictionary<string, object?> CopyCommonRequestParams(Dictionary<string, object?> payload, string protocol)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            "model",
            "messages",
            "input",
            "instructions",
            "system",
            "tools",
            "tool_choice"
        };
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in payload)
        {
            if (!ignored.Contains(key))
            {
                result[key] = DeepCopy(value);
            }
        }

        if (protocol == Responses && result.ContainsKey("max_output_tokens"))
        {
            result["max_tokens"] = result["max_output_tokens"];
            result.Remove("max_output_tokens");
        }

        if (protocol == Chat
            && result.ContainsKey("max_completion_tokens")
            && !result.ContainsKey("max_tokens"))
        {
            result["max_tokens"] = result["max_completion_tokens"];
            result.Remove("max_completion_tokens");
        }

        return result;
    }

    private static void FilterRequestParameters(Dictionary<string, object?> payload, string protocol)
    {
        var allowed = protocol switch
        {
            Responses => ResponsesRequestParameterNames,
            Chat => ChatRequestParameterNames,
            Messages => MessagesRequestParameterNames,
            _ => throw new BadRequestException($"unsupported target protocol: {protocol}")
        };

        foreach (var key in payload.Keys.Where(key => !allowed.Contains(key)).ToList())
        {
            payload.Remove(key);
        }
    }

    private static readonly HashSet<string> ResponsesRequestParameterNames = new(StringComparer.Ordinal)
    {
        "background", "context_management", "conversation", "include", "input", "instructions",
        "max_output_tokens", "max_tool_calls", "metadata", "model", "moderation", "parallel_tool_calls",
        "previous_response_id", "prompt", "prompt_cache_key", "prompt_cache_options", "prompt_cache_retention",
        "reasoning", "safety_identifier", "service_tier", "store", "stream", "stream_options", "temperature",
        "text", "tool_choice", "tools", "top_logprobs", "top_p", "truncation", "user"
    };

    private static readonly HashSet<string> ChatRequestParameterNames = new(StringComparer.Ordinal)
    {
        "messages", "model", "audio", "frequency_penalty", "function_call", "functions", "logit_bias",
        "logprobs", "max_completion_tokens", "max_tokens", "metadata", "modalities", "moderation", "n",
        "parallel_tool_calls", "prediction", "presence_penalty", "prompt_cache_key", "prompt_cache_options",
        "prompt_cache_retention", "reasoning_effort", "response_format", "safety_identifier", "seed",
        "service_tier", "stop", "store", "stream", "stream_options", "temperature", "thinking", "tool_choice",
        "tools", "top_logprobs", "top_p", "user", "verbosity", "web_search_options"
    };

    private static readonly HashSet<string> MessagesRequestParameterNames = new(StringComparer.Ordinal)
    {
        "model", "messages", "max_tokens", "cache_control", "container", "inference_geo", "metadata",
        "output_config", "service_tier", "stop_sequences", "stream", "system", "temperature", "thinking",
        "tool_choice", "tools", "top_k", "top_p", "mcp_servers"
    };

    private static Dictionary<string, object?> ResponsesFormatToChatResponseFormat(Dictionary<string, object?> format)
    {
        var type = GetString(format, "type") ?? "text";
        if (type != "json_schema")
        {
            return Obj(("type", type == "json_object" ? "json_object" : "text"));
        }

        return Obj(
            ("type", "json_schema"),
            ("json_schema", Obj(
                ("name", GetValue(format, "name") ?? "response"),
                ("schema", GetValue(format, "schema") ?? new Dictionary<string, object?>()),
                ("strict", GetValue(format, "strict") ?? true))));
    }

    private static Dictionary<string, object?> ChatResponseFormatToResponsesFormat(Dictionary<string, object?> format)
    {
        var type = GetString(format, "type") ?? "text";
        if (type != "json_schema")
        {
            return Obj(("type", type));
        }

        var jsonSchema = ObjectValue(format, "json_schema");
        return Obj(
            ("type", "json_schema"),
            ("name", GetValue(jsonSchema, "name") ?? "response"),
            ("schema", GetValue(jsonSchema, "schema") ?? new Dictionary<string, object?>()),
            ("strict", GetValue(jsonSchema, "strict") ?? true));
    }

    private static List<object?> AppendSystemInstruction(List<object?> messages, string instruction)
    {
        if (string.IsNullOrEmpty(instruction))
        {
            return messages;
        }

        if (messages.Count > 0 && TryAsObject(messages[0], out var firstMessage) && GetString(firstMessage, "role") == "system")
        {
            var result = messages.Select(DeepCopy).ToList();
            var firstResult = AsObject(result[0]);
            var content = StringifyContent(GetValue(firstResult, "content") ?? string.Empty);
            firstResult["content"] = string.IsNullOrEmpty(content)
                ? instruction
                : $"{content}\n\n{instruction}";
            result[0] = firstResult;
            return result;
        }

        return [Obj(("role", "system"), ("content", instruction)), .. messages];
    }

    private static bool ResponsesPayloadHasPlanModeTag(Dictionary<string, object?> payload)
    {
        var developerInputs = ListValue(payload, "input")
            .Where(item => TryAsObject(item, out var inputItem) && GetString(inputItem, "role") == "developer")
            .Select(DeepCopy)
            .ToList();
        var planModeSource = Obj(
            ("instructions", GetValue(payload, "instructions") ?? string.Empty),
            ("input", developerInputs));
        return StringifyContent(planModeSource).Contains("<proposed_plan>", StringComparison.Ordinal);
    }

}

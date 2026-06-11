using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static Dictionary<string, object?> ToCanonicalRequest(Dictionary<string, object?> payload, string protocol)
    {
        return protocol switch
        {
            Responses => ResponsesRequestToCanonical(payload),
            Chat => ChatRequestToCanonical(payload),
            Messages => MessagesRequestToCanonical(payload),
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

    private static Dictionary<string, object?> ResponsesRequestToCanonical(Dictionary<string, object?> payload)
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
            ("tools", ResponsesToolsToCanonical(GetValue(payload, "tools"))),
            ("tool_choice", GetValue(payload, "tool_choice")),
            ("params", CopyCommonRequestParams(payload, Responses)));
    }

    private static Dictionary<string, object?> ChatRequestToCanonical(Dictionary<string, object?> payload)
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
            ("tools", ChatToolsToCanonical(GetValue(payload, "tools"))),
            ("tool_choice", GetValue(payload, "tool_choice")),
            ("params", CopyCommonRequestParams(payload, Chat)));
    }

    private static Dictionary<string, object?> MessagesRequestToCanonical(Dictionary<string, object?> payload)
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

            messages.Add(Obj(
                ("role", GetString(message, "role") ?? "user"),
                ("content", AnthropicContentToChatContent(GetValue(message, "content") ?? string.Empty))));
        }

        return Obj(
            ("model", GetValue(payload, "model")),
            ("messages", messages),
            ("tools", AnthropicToolsToCanonical(GetValue(payload, "tools"))),
            ("tool_choice", GetValue(payload, "tool_choice")),
            ("params", CopyCommonRequestParams(payload, Messages)));
    }

    private static Dictionary<string, object?> CanonicalToResponsesRequest(Dictionary<string, object?> canonical)
    {
        var result = Obj(("model", GetValue(canonical, "model")));
        MergeInto(result, ObjectValue(canonical, "params"));

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
            result["tool_choice"] = GetValue(canonical, "tool_choice");
        }

        if (result.ContainsKey("max_tokens") && !result.ContainsKey("max_output_tokens"))
        {
            result["max_output_tokens"] = result["max_tokens"];
            result.Remove("max_tokens");
        }

        return result;
    }

    private static Dictionary<string, object?> CanonicalToChatRequest(Dictionary<string, object?> canonical)
    {
        var result = Obj(("model", GetValue(canonical, "model")), ("messages", new List<object?>()));
        MergeInto(result, ObjectValue(canonical, "params"));

        var outputMessages = ListValue(result, "messages");
        foreach (var item in ListValue(canonical, "messages"))
        {
            if (!TryAsObject(item, out var message))
            {
                continue;
            }

            var converted = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in new[] { "role", "content", "tool_calls", "tool_call_id", "name", "reasoning_content" })
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

        return result;
    }

    private static Dictionary<string, object?> CanonicalToMessagesRequest(Dictionary<string, object?> canonical)
    {
        var result = Obj(("model", GetValue(canonical, "model")), ("messages", new List<object?>()));
        MergeInto(result, ObjectValue(canonical, "params"));
        DropResponsesOnlyParamsForMessages(result);

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
                outputMessages.Add(Obj(
                    ("role", "user"),
                    ("content", new List<object?>
                    {
                        Obj(
                            ("type", "tool_result"),
                            ("tool_use_id", GetValue(message, "tool_call_id")),
                            ("content", StringifyContent(GetValue(message, "content") ?? string.Empty)))
                    })));
                continue;
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

        if (HasNonNullValue(canonical, "tool_choice"))
        {
            result["tool_choice"] = GetValue(canonical, "tool_choice");
        }

        if (result.ContainsKey("max_output_tokens") && !result.ContainsKey("max_tokens"))
        {
            result["max_tokens"] = result["max_output_tokens"];
            result.Remove("max_output_tokens");
        }

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
            content.Add(Obj(
                ("type", "tool_use"),
                ("id", GetValue(toolCall, "id")),
                ("name", GetValue(function, "name") ?? GetValue(toolCall, "name")),
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
                     "service_tier",
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

        return result;
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

using OpenCodex.Api.Errors;

namespace OpenCodex.Api.Protocols;

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

            outputMessages.Add(Obj(
                ("role", role),
                ("content", ChatContentToAnthropicContent(GetValue(message, "content") ?? string.Empty))));
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

}

using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static Dictionary<string, object?> ToCanonicalResponse(
        Dictionary<string, object?> payload,
        string protocol,
        string? originalModel)
    {
        return protocol switch
        {
            Responses => ResponsesResponseToCanonical(payload, originalModel),
            Chat => ChatResponseToCanonical(payload, originalModel),
            Messages => MessagesResponseToCanonical(payload, originalModel),
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
            else if (type is not null && ResponsesToolCallTypes.Contains(type))
            {
                var name = GetString(item, "name") ?? type.Replace("_call", string.Empty, StringComparison.Ordinal);
                var arguments = GetValue(item, "arguments") ?? GetValue(item, "input") ?? new Dictionary<string, object?>();
                toolCalls.Add(Obj(
                    ("id", GetValue(item, "call_id") ?? GetValue(item, "id") ?? NewId("call")),
                    ("name", name),
                    ("arguments", JsonDumps(arguments))));
            }
        }

        return Obj(
            ("id", GetValue(payload, "id") ?? NewId("resp")),
            ("model", originalModel ?? GetValue(payload, "model")),
            ("created", GetValue(payload, "created_at") ?? Now()),
            ("text", string.Concat(textParts)),
            ("reasoning", string.Concat(reasoningParts)),
            ("annotations", annotations),
            ("tool_calls", toolCalls),
            ("finish_reason", GetValue(payload, "status") ?? "stop"),
            ("usage", ResponsesUsageToCanonical(ObjectValue(payload, "usage"))),
            ("raw", DeepCopy(payload)));
    }

    private static Dictionary<string, object?> ChatResponseToCanonical(
        Dictionary<string, object?> payload,
        string? originalModel)
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

            var function = ObjectValue(toolCall, "function");
            var toolName = GetString(function, "name");
            var (namespaceName, _) = NamespaceCallParts(toolName);
            toolCalls.Add(Obj(
                ("id", GetValue(toolCall, "id") ?? NewId("call")),
                ("name", toolName),
                ("namespace", namespaceName),
                ("arguments", GetValue(function, "arguments") ?? "{}")));
        }

        return Obj(
            ("id", GetValue(payload, "id") ?? NewId("chatcmpl")),
            ("model", originalModel ?? GetValue(payload, "model")),
            ("created", GetValue(payload, "created") ?? Now()),
            ("text", StringifyContent(GetValue(message, "content") ?? string.Empty)),
            ("reasoning", StringifyContent(GetValue(message, "reasoning_content") ?? string.Empty)),
            ("annotations", NormalizeAnnotations(GetValue(message, "annotations"))),
            ("tool_calls", toolCalls),
            ("finish_reason", GetValue(choice, "finish_reason") ?? "stop"),
            ("usage", ChatUsageToCanonical(ObjectValue(payload, "usage"))),
            ("raw", DeepCopy(payload)));
    }

    private static Dictionary<string, object?> MessagesResponseToCanonical(
        Dictionary<string, object?> payload,
        string? originalModel)
    {
        var textParts = new List<string>();
        var toolCalls = new List<object?>();
        foreach (var contentItem in ListValue(payload, "content"))
        {
            if (!TryAsObject(contentItem, out var block))
            {
                continue;
            }

            if (GetString(block, "type") == "text")
            {
                textParts.Add(Convert.ToString(GetValue(block, "text")) ?? string.Empty);
            }
            else if (GetString(block, "type") == "tool_use")
            {
                toolCalls.Add(Obj(
                    ("id", GetValue(block, "id") ?? NewId("call")),
                    ("name", GetValue(block, "name")),
                    ("arguments", JsonDumps(GetValue(block, "input") ?? new Dictionary<string, object?>()))));
            }
        }

        return Obj(
            ("id", GetValue(payload, "id") ?? NewId("msg")),
            ("model", originalModel ?? GetValue(payload, "model")),
            ("created", Now()),
            ("text", string.Concat(textParts)),
            ("tool_calls", toolCalls),
            ("finish_reason", GetValue(payload, "stop_reason") ?? "stop"),
            ("usage", MessagesUsageToCanonical(ObjectValue(payload, "usage"))),
            ("raw", DeepCopy(payload)));
    }

    private static Dictionary<string, object?> CanonicalToResponsesResponse(Dictionary<string, object?> canonical)
    {
        var output = new List<object?>();
        var reasoning = StringifyContent(GetValue(canonical, "reasoning") ?? string.Empty);
        if (!string.IsNullOrEmpty(reasoning))
        {
            output.Add(Obj(
                ("id", NewId("rs")),
                ("type", "reasoning"),
                ("status", "completed"),
                ("summary", new List<object?> { Obj(("type", "summary_text"), ("text", reasoning)) }),
                ("encrypted_content", reasoning)));
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

        foreach (var toolCallItem in ListValue(canonical, "tool_calls"))
        {
            if (!TryAsObject(toolCallItem, out var toolCall))
            {
                continue;
            }

            output.Add(ResponsesToolCallItemFromToolCall(
                GetValue(toolCall, "id"),
                GetValue(toolCall, "name"),
                GetValue(toolCall, "arguments") ?? "{}",
                GetValue(toolCall, "namespace")));
        }

        var finishReason = GetString(canonical, "finish_reason") ?? "stop";
        var incomplete = finishReason == "length";
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
            response["incomplete_details"] = Obj(("reason", "max_output_tokens"));
        }

        return response;
    }

    private static Dictionary<string, object?> CanonicalToChatResponse(Dictionary<string, object?> canonical)
    {
        var message = Obj(
            ("role", "assistant"),
            ("content", IsTruthy(GetValue(canonical, "text")) ? GetValue(canonical, "text") : null));

        var canonicalToolCalls = ListValue(canonical, "tool_calls");
        if (canonicalToolCalls.Count > 0)
        {
            message["tool_calls"] = canonicalToolCalls
                .Where(item => TryAsObject(item, out _))
                .Select(item =>
                {
                    var toolCall = AsObject(item);
                    return (object?)Obj(
                        ("id", GetValue(toolCall, "id")),
                        ("type", "function"),
                        ("function", Obj(
                            ("name", GetValue(toolCall, "name")),
                            ("arguments", GetValue(toolCall, "arguments") ?? "{}"))));
                })
                .ToList();
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

            content.Add(Obj(
                ("type", "tool_use"),
                ("id", GetValue(toolCall, "id")),
                ("name", GetValue(toolCall, "name")),
                ("input", ParseJsonObject(GetValue(toolCall, "arguments") ?? "{}"))));
        }

        return Obj(
            ("id", GetValue(canonical, "id") ?? NewId("msg")),
            ("type", "message"),
            ("role", "assistant"),
            ("model", GetValue(canonical, "model")),
            ("content", content),
            ("stop_reason", GetValue(canonical, "finish_reason") ?? "end_turn"),
            ("stop_sequence", null),
            ("usage", CanonicalUsageToMessages(ObjectValue(canonical, "usage"))));
    }
}

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static List<object?> NormalizeChatToolHistory(List<object?> messages)
    {
        var normalized = FoldReasoningIntoToolCallMessages(messages);
        normalized = MergeConsecutiveAssistantToolCallMessages(normalized);
        RemoveOrphanToolMessages(normalized);
        EnsureToolCallsHaveOutputs(normalized);
        return normalized;
    }

    private static List<object?> FoldReasoningIntoToolCallMessages(List<object?> messages)
    {
        var folded = new List<object?>();
        Dictionary<string, object?>? pendingReasoning = null;
        foreach (var item in messages)
        {
            if (!TryAsObject(item, out var message))
            {
                continue;
            }

            if (IsReasoningOnlyMessage(message))
            {
                if (folded.Count > 0 && TryAsObject(folded[^1], out var previous) && IsAssistantWithToolCalls(previous))
                {
                    AppendReasoningContent(previous, GetValue(message, "reasoning_content"));
                }
                else if (pendingReasoning is null)
                {
                    pendingReasoning = AsObject(DeepCopy(message));
                }
                else
                {
                    AppendReasoningContent(pendingReasoning, GetValue(message, "reasoning_content"));
                }

                continue;
            }

            if (IsAssistantWithToolCalls(message) && pendingReasoning is not null)
            {
                message = AsObject(DeepCopy(message));
                AppendReasoningContent(message, GetValue(pendingReasoning, "reasoning_content"));
                pendingReasoning = null;
            }
            else if (pendingReasoning is not null)
            {
                folded.Add(pendingReasoning);
                pendingReasoning = null;
            }

            folded.Add(message);
        }

        if (pendingReasoning is not null)
        {
            folded.Add(pendingReasoning);
        }

        return folded;
    }

    private static List<object?> MergeConsecutiveAssistantToolCallMessages(List<object?> messages)
    {
        var merged = new List<object?>();
        Dictionary<string, object?>? pending = null;
        foreach (var item in messages)
        {
            if (!TryAsObject(item, out var message))
            {
                continue;
            }

            if (IsAssistantToolCallOnlyMessage(message))
            {
                if (pending is null)
                {
                    pending = AsObject(DeepCopy(message));
                }
                else
                {
                    ListValue(pending, "tool_calls").AddRange(ListValue(message, "tool_calls").Select(DeepCopy));
                }

                continue;
            }

            if (pending is not null)
            {
                merged.Add(pending);
                pending = null;
            }

            merged.Add(message);
        }

        if (pending is not null)
        {
            merged.Add(pending);
        }

        return merged;
    }

    private static void RemoveOrphanToolMessages(List<object?> messages)
    {
        HashSet<string>? validIds = null;
        var index = 0;
        while (index < messages.Count)
        {
            if (!TryAsObject(messages[index], out var message))
            {
                index++;
                continue;
            }

            var role = GetString(message, "role");
            if (role == "assistant")
            {
                var toolCalls = ListValue(message, "tool_calls");
                validIds = toolCalls.Count > 0
                    ? toolCalls
                        .Where(item => TryAsObject(item, out var toolCall) && HasNonNullValue(toolCall, "id"))
                        .Select(item => Convert.ToString(GetValue(AsObject(item), "id")) ?? string.Empty)
                        .Where(id => !string.IsNullOrEmpty(id))
                        .ToHashSet(StringComparer.Ordinal)
                    : null;
                index++;
                continue;
            }

            if (role == "tool")
            {
                var toolCallId = Convert.ToString(GetValue(message, "tool_call_id")) ?? string.Empty;
                if (validIds is not null && validIds.Contains(toolCallId))
                {
                    index++;
                    continue;
                }

                messages.RemoveAt(index);
                continue;
            }

            validIds = null;
            index++;
        }
    }

    private static void EnsureToolCallsHaveOutputs(List<object?> messages)
    {
        var index = 0;
        while (index < messages.Count)
        {
            if (!TryAsObject(messages[index], out var message) || !IsAssistantWithToolCalls(message))
            {
                index++;
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var insertAt = index + 1;
            while (insertAt < messages.Count
                   && TryAsObject(messages[insertAt], out var toolMessage)
                   && GetString(toolMessage, "role") == "tool")
            {
                var toolCallId = Convert.ToString(GetValue(toolMessage, "tool_call_id"));
                if (!string.IsNullOrEmpty(toolCallId))
                {
                    seen.Add(toolCallId);
                }

                insertAt++;
            }

            var missing = ListValue(message, "tool_calls")
                .Where(item => TryAsObject(item, out var toolCall) && HasNonNullValue(toolCall, "id"))
                .Select(item => Convert.ToString(GetValue(AsObject(item), "id")) ?? string.Empty)
                .Where(id => !string.IsNullOrEmpty(id) && !seen.Contains(id))
                .ToList();

            if (missing.Count > 0)
            {
                var placeholders = missing
                    .Select(id => (object?)Obj(
                        ("role", "tool"),
                        ("tool_call_id", id),
                        ("content", MissingToolOutputMessage)))
                    .ToList();
                messages.InsertRange(insertAt, placeholders);
                index = insertAt + placeholders.Count;
                continue;
            }

            index++;
        }
    }

    private static bool IsReasoningOnlyMessage(Dictionary<string, object?> message)
    {
        return GetString(message, "role") == "assistant"
               && IsTruthy(GetValue(message, "reasoning_content"))
               && IsEmptyChatContent(GetValue(message, "content"))
               && ListValue(message, "tool_calls").Count == 0;
    }

    private static bool IsAssistantWithToolCalls(Dictionary<string, object?> message)
    {
        return GetString(message, "role") == "assistant" && ListValue(message, "tool_calls").Count > 0;
    }

    private static bool IsAssistantToolCallOnlyMessage(Dictionary<string, object?> message)
    {
        return GetString(message, "role") == "assistant"
               && IsEmptyChatContent(GetValue(message, "content"))
               && ListValue(message, "tool_calls").Count > 0;
    }
}

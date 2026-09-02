namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static List<object?> NormalizeChatToolHistory(List<object?> messages)
    {
        var normalized = FoldReasoningIntoToolCallMessages(messages);
        normalized = MergeConsecutiveAssistantToolCallMessages(normalized);
        normalized = MergeAssistantTextWithToolCalls(normalized);
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
                if (folded.Count > 0 && TryAsObject(folded[^1], out var previous) && GetString(previous, "role") == "assistant")
                {
                    AppendReasoningContent(previous, GetValue(message, "reasoning_content"));
                    MergeThinkingEncrypted(previous, message);
                }
                else if (pendingReasoning is null)
                {
                    pendingReasoning = AsObject(DeepCopy(message));
                }
                else
                {
                    AppendReasoningContent(pendingReasoning, GetValue(message, "reasoning_content"));
                    MergeThinkingEncrypted(pendingReasoning, message);
                }

                continue;
            }

            if (GetString(message, "role") == "assistant" && pendingReasoning is not null)
            {
                message = AsObject(DeepCopy(message));
                AppendReasoningContent(message, GetValue(pendingReasoning, "reasoning_content"));
                MergeThinkingEncrypted(message, pendingReasoning);
                pendingReasoning = null;
            }
            else if (pendingReasoning is not null)
            {
                // reasoning 后面不是 assistant（如 user 消息），没有关联响应，丢弃避免产生空 content 消息
                pendingReasoning = null;
            }

            folded.Add(message);
        }

        // 末尾孤儿 reasoning 丢弃：没有后续 assistant 可关联，保留只会产生空 content 消息

        return folded;
    }

    /// <summary>
    /// 将 source 消息的 anthropic_thinking_encrypted（带签名的 thinking block）
    /// 合并到 target 消息中。双方都有时合并 thinking blocks 数组后重新编码；
    /// 解码失败时保守保留 target 已有的内容。
    /// </summary>
    private static void MergeThinkingEncrypted(
        Dictionary<string, object?> target,
        Dictionary<string, object?> source)
    {
        var sourceEncrypted = GetString(source, "anthropic_thinking_encrypted") ?? string.Empty;
        if (string.IsNullOrEmpty(sourceEncrypted))
        {
            return;
        }

        var targetEncrypted = GetString(target, "anthropic_thinking_encrypted") ?? string.Empty;
        if (string.IsNullOrEmpty(targetEncrypted))
        {
            target["anthropic_thinking_encrypted"] = sourceEncrypted;
            return;
        }

        if (TryDecodeAnthropicThinkingBlocks(targetEncrypted, out var targetBlocks)
            && TryDecodeAnthropicThinkingBlocks(sourceEncrypted, out var sourceBlocks))
        {
            var merged = new List<object?>(targetBlocks);
            merged.AddRange(sourceBlocks);
            target["anthropic_thinking_encrypted"] = EncodeAnthropicThinkingBlocks(merged);
        }
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

    /// <summary>
    /// 把同一 assistant 回合被拆开的正文与工具调用并回一条消息。
    /// Responses 协议把一个回合的输出文本与 function_call 存成彼此独立的 item，
    /// 若原样拆成两条 assistant 消息，上游会当成两个回合，
    /// 折叠好的思考内容也就和真正发起调用的那条脱节。
    /// </summary>
    private static List<object?> MergeAssistantTextWithToolCalls(List<object?> messages)
    {
        var merged = new List<object?>();
        foreach (var item in messages)
        {
            if (!TryAsObject(item, out var message))
            {
                continue;
            }

            if (IsAssistantToolCallOnlyMessage(message)
                && merged.Count > 0
                && TryAsObject(merged[^1], out var previous)
                && IsAssistantTextOnlyMessage(previous))
            {
                var target = AsObject(DeepCopy(previous));
                target["tool_calls"] = ListValue(message, "tool_calls").Select(DeepCopy).ToList();
                AppendReasoningContent(target, GetValue(message, "reasoning_content"));
                MergeThinkingEncrypted(target, message);
                merged[^1] = target;
                continue;
            }

            merged.Add(message);
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

    private static bool IsAssistantTextOnlyMessage(Dictionary<string, object?> message)
    {
        return GetString(message, "role") == "assistant"
               && !IsEmptyChatContent(GetValue(message, "content"))
               && ListValue(message, "tool_calls").Count == 0;
    }
}

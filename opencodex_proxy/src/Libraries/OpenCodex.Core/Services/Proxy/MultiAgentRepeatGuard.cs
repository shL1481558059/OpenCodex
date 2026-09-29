using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 子代理重复轮次的软性护栏：同一任务超过阈值后，在指令中追加收尾提醒。
/// </summary>
/// <remarks>
/// 客户端侧多代理没有服务端停止条件，实测出现过同一子任务连续数十轮重复工具调用的情况。
/// 该护栏只追加指令，不修改消息序列、不终止请求，因此不会破坏客户端与上游的协议往返。
/// </remarks>
public sealed class MultiAgentRepeatGuard
{
    public const int TurnLimit = 12;

    private const int MaxEntries = 4096;
    private const string ReminderTag = "multi_agent_repeat_guard";

    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, CounterEntry> _entries = new(StringComparer.Ordinal);

    public Dictionary<string, object?> Apply(
        string conversationKey,
        Dictionary<string, object?> payload)
    {
        var context = MultiAgentTurnContext.Resolve(payload);
        if (!context.IsSubAgentTurn)
        {
            return payload;
        }

        var turns = Increment(CounterKey(conversationKey, context.TaskMessageId));
        if (turns <= TurnLimit)
        {
            return payload;
        }

        payload["instructions"] = JsonDictionaryValue.String(payload, "instructions")
            + Reminder(context, turns);
        return payload;
    }

    private static string CounterKey(string conversationKey, string taskMessageId)
    {
        var scope = conversationKey.Length > 0 ? conversationKey : "-";
        return scope + "|" + taskMessageId;
    }

    private static string Reminder(MultiAgentTurnContext context, int turns)
    {
        return $"\n\n<{ReminderTag} agent=\"{context.AgentName}\" turn=\"{turns}\">"
            + $"You have already taken {turns} turns on this sub-task. "
            + "Stop repeating the same tool calls, summarize what you have so far, "
            + "and return your final answer now."
            + $"</{ReminderTag}>";
    }

    private int Increment(string key)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            Prune(now);
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Count++;
                entry.LastSeen = now;
                return entry.Count;
            }

            _entries[key] = new CounterEntry { Count = 1, LastSeen = now };
            return 1;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        var expired = _entries
            .Where(pair => now - pair.Value.LastSeen > EntryTtl)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in expired)
        {
            _entries.Remove(key);
        }

        if (_entries.Count < MaxEntries)
        {
            return;
        }

        // 超出上限时按最后访问时间淘汰最旧的四分之一，保证内存占用有界。
        var evictCount = Math.Max(1, _entries.Count / 4);
        var oldest = _entries
            .OrderBy(pair => pair.Value.LastSeen)
            .Take(evictCount)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in oldest)
        {
            _entries.Remove(key);
        }
    }

    private sealed class CounterEntry
    {
        public int Count { get; set; }

        public DateTimeOffset LastSeen { get; set; }
    }
}

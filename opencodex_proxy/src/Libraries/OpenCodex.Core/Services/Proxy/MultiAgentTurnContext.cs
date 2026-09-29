using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 客户端侧多代理请求的归属信息：本轮由哪个代理执行，以及对应任务消息的标识。
/// </summary>
/// <remarks>
/// Codex 客户端侧多代理把代理间通信写成 agent_message 输入项，收件人即本轮执行者。
/// 同一收件人的第一条 agent_message 是该代理的任务分配消息，在后续多轮请求中保持稳定，
/// 因此可以作为“同一任务已执行多少轮”的计数键。
/// </remarks>
public sealed class MultiAgentTurnContext
{
    private const string RootAgentName = "/root";

    private MultiAgentTurnContext()
    {
    }

    public string AgentName { get; private init; } = string.Empty;

    public string TaskMessageId { get; private init; } = string.Empty;

    public string TaskAuthor { get; private init; } = string.Empty;

    public string TaskRecipient { get; private init; } = string.Empty;

    public bool IsSubAgentTurn =>
        TaskMessageId.Length > 0
        && AgentName.Length > 0
        && !string.Equals(AgentName, RootAgentName, StringComparison.Ordinal);

    public static MultiAgentTurnContext Resolve(IReadOnlyDictionary<string, object?> payload)
    {
        var firstTaskByRecipient = new Dictionary<string, (string Id, string Author)>(StringComparer.Ordinal);
        var agentName = string.Empty;

        foreach (var item in JsonDictionaryValue.List(payload, "input"))
        {
            if (item is not IReadOnlyDictionary<string, object?> inputItem
                || !string.Equals(
                    JsonDictionaryValue.String(inputItem, "type"),
                    "agent_message",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var recipient = JsonDictionaryValue.String(inputItem, "recipient");
            if (recipient.Length == 0)
            {
                continue;
            }

            // 最后一条 agent_message 的收件人即本轮执行者。
            agentName = recipient;
            var id = JsonDictionaryValue.String(inputItem, "id");
            if (id.Length > 0 && !firstTaskByRecipient.ContainsKey(recipient))
            {
                firstTaskByRecipient[recipient] = (id, JsonDictionaryValue.String(inputItem, "author"));
            }
        }

        var context = new MultiAgentTurnContext
        {
            AgentName = agentName
        };
        if (agentName.Length > 0 && firstTaskByRecipient.TryGetValue(agentName, out var task))
        {
            context = new MultiAgentTurnContext
            {
                AgentName = agentName,
                TaskMessageId = task.Id,
                TaskAuthor = task.Author,
                TaskRecipient = agentName
            };
        }

        return context;
    }
}

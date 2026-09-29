using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.Proxy;

public enum MultiAgentV2Action
{
    None,
    Passthrough,
    Downgrade,
    Reject
}

public static class MultiAgentV2Policy
{
    public const string CompatKey = "multi_agent_v2_mode";

    private const string AssignAgentTaskToolName = "assign_agent_task";

    private static readonly HashSet<string> MultiAgentItemTypes = new(StringComparer.Ordinal)
    {
        "agent_message",
        "multi_agent_call",
        "multi_agent_call_output"
    };

    public static MultiAgentV2Action Resolve(
        string entryProtocol,
        string channelType,
        IReadOnlyDictionary<string, object?> channel,
        IReadOnlyDictionary<string, object?> payload)
    {
        if (!IsV2Request(payload))
        {
            return MultiAgentV2Action.None;
        }

        var configured = ReadConfiguredAction(channel);
        if (configured != MultiAgentV2Action.None)
        {
            return configured;
        }

        if (entryProtocol == ProtocolConverter.Responses
            && channelType == ProtocolConverter.Responses
            && IsOfficialOpenAiChannel(channel))
        {
            return MultiAgentV2Action.Passthrough;
        }

        // chat/messages 上游必须做协议转换，hosted 多代理语义无法保留；默认降级以保持客户端侧多代理可用。
        if (channelType is ProtocolConverter.Chat or ProtocolConverter.Messages)
        {
            return MultiAgentV2Action.Downgrade;
        }

        return MultiAgentV2Action.Reject;
    }

    public static bool IsV2Request(IReadOnlyDictionary<string, object?> payload)
    {
        if (JsonDictionaryValue.Get(payload, "multi_agent") is IReadOnlyDictionary<string, object?> multiAgent
            && multiAgent.Count > 0)
        {
            return !multiAgent.TryGetValue("enabled", out var enabled) || enabled is true;
        }

        foreach (var item in JsonDictionaryValue.List(payload, "input"))
        {
            if (item is IReadOnlyDictionary<string, object?> inputItem
                && MultiAgentItemTypes.Contains(JsonDictionaryValue.String(inputItem, "type")))
            {
                return true;
            }
        }

        foreach (var tool in JsonDictionaryValue.List(payload, "tools"))
        {
            if (tool is IReadOnlyDictionary<string, object?> toolItem
                && string.Equals(
                    JsonDictionaryValue.String(toolItem, "name"),
                    AssignAgentTaskToolName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static MultiAgentV2Action ReadConfiguredAction(IReadOnlyDictionary<string, object?> channel)
    {
        var compat = JsonDictionaryValue.Object(channel, "compat", WebSearchPayload.DeepCopyObject);
        return JsonDictionaryValue.String(compat, CompatKey).ToLowerInvariant() switch
        {
            "passthrough" => MultiAgentV2Action.Passthrough,
            "downgrade" => MultiAgentV2Action.Downgrade,
            "reject" => MultiAgentV2Action.Reject,
            _ => MultiAgentV2Action.None
        };
    }

    private static bool IsOfficialOpenAiChannel(IReadOnlyDictionary<string, object?> channel)
    {
        var baseUrl = JsonDictionaryValue.String(channel, "baseurl");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IsOfficialHost(uri.Host, "openai.com") || IsOfficialHost(uri.Host, "chatgpt.com");
    }

    private static bool IsOfficialHost(string host, string officialDomain)
    {
        return host.Equals(officialDomain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{officialDomain}", StringComparison.OrdinalIgnoreCase);
    }
}

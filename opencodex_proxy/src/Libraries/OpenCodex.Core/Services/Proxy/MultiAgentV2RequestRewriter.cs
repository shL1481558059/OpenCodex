using System.Text;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.Proxy;

public static class MultiAgentV2RequestRewriter
{
    // 官方 Responses hosted 多代理使用 enc_ 前缀的密文；Codex 客户端侧多代理把明文 payload 写进同一字段。
    private const string EncryptedContentPrefix = "enc_";

    private const string EmptyAgentMessagePlaceholder =
        "[agent message carried no forwardable content]";

    private static readonly HashSet<string> HostedToolTypes = new(StringComparer.Ordinal)
    {
        "multi_agent",
        "multi_agent_call",
        "multi_agent_call_output"
    };

    public static Dictionary<string, object?> Apply(
        IReadOnlyDictionary<string, object?> payload,
        MultiAgentV2Action action)
    {
        if (action != MultiAgentV2Action.Downgrade)
        {
            return payload as Dictionary<string, object?> ?? WebSearchPayload.DeepCopyObject(payload);
        }

        var result = WebSearchPayload.DeepCopyObject(payload);
        result.Remove("multi_agent");

        if (JsonDictionaryValue.Get(result, "input") is IEnumerable<object?> inputItems)
        {
            result["input"] = inputItems.Select(RewriteInputItem).ToList();
        }

        if (JsonDictionaryValue.Get(result, "tools") is IEnumerable<object?> tools)
        {
            var filtered = tools
                .Where(tool => tool is not IReadOnlyDictionary<string, object?> toolItem
                    || !HostedToolTypes.Contains(JsonDictionaryValue.String(toolItem, "type")))
                .ToList();
            if (filtered.Count == 0)
            {
                result.Remove("tools");
            }
            else
            {
                result["tools"] = filtered;
            }
        }

        return result;
    }

    private static object? RewriteInputItem(object? item)
    {
        if (item is not IReadOnlyDictionary<string, object?> inputItem)
        {
            return item;
        }

        return JsonDictionaryValue.String(inputItem, "type") switch
        {
            "agent_message" => AgentMessageToMessage(inputItem),
            "multi_agent_call" => HostedCallToMessage(inputItem, "multi_agent_call"),
            "multi_agent_call_output" => HostedCallToMessage(inputItem, "multi_agent_call_output"),
            _ => item
        };
    }

    private static Dictionary<string, object?> AgentMessageToMessage(
        IReadOnlyDictionary<string, object?> item)
    {
        var content = new List<object?>();
        foreach (var block in JsonDictionaryValue.List(item, "content"))
        {
            if (block is not IReadOnlyDictionary<string, object?> blockItem)
            {
                continue;
            }

            var type = JsonDictionaryValue.String(blockItem, "type");
            if (type == "encrypted_content")
            {
                AppendEncryptedContent(
                    content,
                    item,
                    JsonDictionaryValue.String(blockItem, "encrypted_content"));
                continue;
            }

            if (type is not ("input_text" or "text" or "output_text"))
            {
                continue;
            }

            var text = JsonDictionaryValue.String(blockItem, "text");
            if (text.Length > 0)
            {
                AppendText(content, text);
            }
        }

        if (content.Count == 0)
        {
            AppendText(content, EmptyAgentMessagePlaceholder);
        }

        var message = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = content
        };
        var id = JsonDictionaryValue.String(item, "id");
        if (id.Length > 0)
        {
            message["id"] = id;
        }

        return message;
    }

    private static void AppendEncryptedContent(
        List<object?> content,
        IReadOnlyDictionary<string, object?> item,
        string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        // 只有带 enc_ 前缀的值才是官方密文；其余取值为客户端写入的明文 payload，必须保留。
        AppendText(
            content,
            value.StartsWith(EncryptedContentPrefix, StringComparison.Ordinal)
                ? EncryptedContentPlaceholder(item)
                : value);
    }

    private static string EncryptedContentPlaceholder(IReadOnlyDictionary<string, object?> item)
    {
        return "[agent message author=\""
            + JsonDictionaryValue.String(item, "author")
            + "\" recipient=\""
            + JsonDictionaryValue.String(item, "recipient")
            + "\" content is encrypted by the Responses API and cannot be forwarded through a chat upstream]";
    }

    private static void AppendText(List<object?> content, string text)
    {
        content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "input_text",
            ["text"] = text
        });
    }

    private static Dictionary<string, object?> HostedCallToMessage(
        IReadOnlyDictionary<string, object?> item,
        string itemType)
    {
        var text = new StringBuilder()
            .Append('[')
            .Append(itemType)
            .Append("] action=")
            .Append(JsonDictionaryValue.String(item, "action"))
            .Append(" call_id=")
            .Append(JsonDictionaryValue.String(item, "call_id"))
            .Append(" agent=")
            .Append(ReadAgentName(item))
            .ToString();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "input_text",
                    ["text"] = text
                }
            }
        };
    }

    private static string ReadAgentName(IReadOnlyDictionary<string, object?> item)
    {
        return JsonDictionaryValue.Get(item, "agent") is IReadOnlyDictionary<string, object?> agent
            ? JsonDictionaryValue.String(agent, "agent_name")
            : string.Empty;
    }
}

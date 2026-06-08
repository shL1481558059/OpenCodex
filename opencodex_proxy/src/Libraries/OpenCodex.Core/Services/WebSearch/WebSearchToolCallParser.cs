using OpenCodex.Core.Protocols;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

public static class WebSearchToolCallParser
{
    public static List<WebSearchToolCall> ExtractToolCalls(
        Dictionary<string, object?> payload,
        string protocol)
    {
        return protocol switch
        {
            ProtocolConverter.Chat => ExtractChatToolCalls(payload),
            ProtocolConverter.Messages => ExtractMessagesToolCalls(payload),
            _ => []
        };
    }

    public static List<WebSearchToolCall> ExtractChatToolCalls(Dictionary<string, object?> payload)
    {
        var result = new List<WebSearchToolCall>();
        var choice = FirstObject(ListValue(payload, "choices"));
        if (choice is null)
        {
            return result;
        }

        var message = ObjectValue(choice, "message");
        var index = 0;
        foreach (var toolCallItem in ListValue(message, "tool_calls"))
        {
            if (!TryAsObject(toolCallItem, out var toolCall))
            {
                continue;
            }

            var function = ObjectValue(toolCall, "function");
            result.Add(new WebSearchToolCall(
                StringValue(toolCall, "id", $"call_{Guid.NewGuid():N}"),
                index++,
                StringValue(function, "name"),
                StringValue(function, "arguments", "{}"),
                DeepCopyObject(toolCall)));
        }

        return result;
    }

    public static List<WebSearchToolCall> ExtractMessagesToolCalls(Dictionary<string, object?> payload)
    {
        var result = new List<WebSearchToolCall>();
        var index = 0;
        foreach (var blockItem in ListValue(payload, "content"))
        {
            if (!TryAsObject(blockItem, out var block)
                || !string.Equals(StringValue(block, "type"), "tool_use", StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new WebSearchToolCall(
                StringValue(block, "id", $"call_{Guid.NewGuid():N}"),
                index++,
                StringValue(block, "name"),
                JsonDumps(GetValue(block, "input") ?? new Dictionary<string, object?>()),
                DeepCopyObject(block)));
        }

        return result;
    }
}

public sealed class WebSearchToolCall
{
    public WebSearchToolCall(
        string id,
        int index,
        string name,
        string arguments,
        Dictionary<string, object?> raw)
    {
        Id = id;
        Index = index;
        Name = name;
        Arguments = arguments;
        Raw = raw;
    }

    public string Id { get; }

    public int Index { get; }

    public string Name { get; }

    public string Arguments { get; }

    public Dictionary<string, object?> Raw { get; }
}

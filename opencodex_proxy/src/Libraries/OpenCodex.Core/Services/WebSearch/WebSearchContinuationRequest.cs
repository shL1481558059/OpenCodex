using OpenCodex.Core.Protocols;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

internal static class WebSearchContinuationRequest
{
    public static Dictionary<string, object?> AppendToolResults(
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> upstreamResponse,
        string protocol,
        IReadOnlyList<WebSearchToolResult> results,
        bool forceFinalAnswer = false)
    {
        var request = DeepCopyObject(upstreamRequest);
        RelaxToolChoice(request);
        if (forceFinalAnswer)
        {
            RemoveWebSearchTool(request);
        }

        if (protocol == ProtocolConverter.Chat)
        {
            var messages = ListValue(request, "messages");
            request["messages"] = messages;
            var choice = FirstObject(ListValue(upstreamResponse, "choices"));
            var message = choice is null ? [] : ObjectValue(choice, "message");
            var assistant = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in new[] { "role", "content", "tool_calls", "reasoning_content" })
            {
                if (message.TryGetValue(key, out var value))
                {
                    assistant[key] = DeepCopy(value);
                }
            }

            messages.Add(assistant);
            foreach (var result in results)
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = result.CallId,
                    ["content"] = result.ToolResult
                });
            }

            return request;
        }

        if (protocol == ProtocolConverter.Messages)
        {
            var messages = ListValue(request, "messages");
            request["messages"] = messages;
            var content = ListValue(upstreamResponse, "content");
            if (content.Count > 0)
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = DeepCopy(content)
                });
            }

            foreach (var result in results)
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = result.CallId,
                            ["content"] = result.ToolResult
                        }
                    }
                });
            }
        }

        return request;
    }

    private static void RelaxToolChoice(Dictionary<string, object?> request)
    {
        request.Remove("tool_choice");
    }

    private static void RemoveWebSearchTool(Dictionary<string, object?> request)
    {
        var tools = ListValue(request, "tools");
        if (tools.Count == 0)
        {
            return;
        }

        var filtered = tools
            .Where(tool => !IsWebSearchTool(tool))
            .ToList();
        if (filtered.Count == 0)
        {
            request.Remove("tools");
            return;
        }

        request["tools"] = filtered;
    }

    private static bool IsWebSearchTool(object? value)
    {
        if (!TryAsObject(value, out var tool))
        {
            return false;
        }

        if (StringValue(tool, "name") == WebSearchRequestPolicy.ToolName)
        {
            return true;
        }

        var function = ObjectValue(tool, "function");
        return StringValue(function, "name") == WebSearchRequestPolicy.ToolName;
    }
}

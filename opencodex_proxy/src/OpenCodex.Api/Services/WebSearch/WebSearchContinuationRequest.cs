using OpenCodex.Api.Protocols;
using static OpenCodex.Api.Abstractions.WebSearchPayload;

namespace OpenCodex.Api.Services.WebSearch;

internal static class WebSearchContinuationRequest
{
    public static Dictionary<string, object?> AppendToolResults(
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> upstreamResponse,
        string protocol,
        IReadOnlyList<WebSearchToolResult> results)
    {
        var request = DeepCopyObject(upstreamRequest);
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
}

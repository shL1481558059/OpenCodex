using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Domain.WebSearch;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

internal static class WebSearchContinuationRequest
{
    public static Dictionary<string, object?> AppendToolResults(
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> upstreamResponse,
        string protocol,
        IReadOnlyList<WebSearchToolResult> results,
        bool forceFinalAnswer = false,
        string internalToolName = WebSearchRequestPolicy.ToolName)
    {
        var request = DeepCopyObject(upstreamRequest);
        RelaxToolChoice(request, internalToolName);
        if (forceFinalAnswer)
        {
            RemoveWebSearchTool(request, internalToolName);
        }

        if (protocol == ProtocolConverter.Chat)
        {
            var messages = ListValue(request, "messages");
            request["messages"] = messages;
            var choice = FirstObject(ListValue(upstreamResponse, "choices"));
            var message = choice is null ? [] : ObjectValue(choice, "message");
            messages.Add(DeepCopyObject(message));
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

            if (results.Count > 0)
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = results.Select(result => (object?)new Dictionary<string, object?>
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = result.CallId,
                            ["content"] = result.ToolResult,
                            ["is_error"] = result.Status != "completed"
                        }).ToList()
                });
            }
        }

        return request;
    }

    private static void RelaxToolChoice(Dictionary<string, object?> request, string internalToolName)
    {
        var choice = GetValue(request, "tool_choice");
        if (choice is "required" or "any")
        {
            request.Remove("tool_choice");
        }
        else if (TryAsObject(choice, out var value))
        {
            var type = StringValue(value, "type");
            var name = StringValue(value, "name", StringValue(ObjectValue(value, "function"), "name"));
            if (type == "allowed_tools")
            {
                if (StringValue(value, "mode") == "required")
                {
                    value["mode"] = "auto";
                }
            }
            else if (name == internalToolName || type is "required" or "any")
            {
                if (value.Count == 1 || name == internalToolName)
                {
                    request.Remove("tool_choice");
                }
                else
                {
                    value["type"] = "auto";
                }
            }
        }
    }

    private static void RemoveWebSearchTool(Dictionary<string, object?> request, string internalToolName)
    {
        var tools = ListValue(request, "tools");
        if (tools.Count == 0)
        {
            return;
        }

        var filtered = tools
            .Where(tool => !IsWebSearchTool(tool, internalToolName))
            .ToList();
        if (filtered.Count == 0)
        {
            request.Remove("tools");
            request.Remove("tool_choice");
            return;
        }

        request["tools"] = filtered;
    }

    private static bool IsWebSearchTool(object? value, string internalToolName)
    {
        if (!TryAsObject(value, out var tool))
        {
            return false;
        }

        if (StringValue(tool, "name") == internalToolName)
        {
            return true;
        }

        var function = ObjectValue(tool, "function");
        return StringValue(function, "name") == internalToolName;
    }
}

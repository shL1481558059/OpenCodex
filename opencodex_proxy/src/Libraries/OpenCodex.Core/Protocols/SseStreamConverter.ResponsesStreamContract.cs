using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    private static Dictionary<string, object?> BuildCapturedResponsesUpstreamResponse(
        ResponsesStreamResponseAccumulator responseAccumulator,
        string eventType,
        string responseId,
        long createdAt,
        string status,
        string responseModel,
        IEnumerable<Dictionary<string, object?>> outputItems,
        Dictionary<string, object?> usage,
        bool preserveCapturedOutputAndUsage = false)
    {
        var response = responseAccumulator.BuildResponse()
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        response["id"] = responseId;
        response["object"] = "response";
        response["status"] = status;
        response["model"] = responseModel;
        if (preserveCapturedOutputAndUsage)
        {
            response.TryAdd("output", outputItems.Cast<object?>().ToList());
            response.TryAdd("usage", usage);
        }
        else
        {
            response["created_at"] = createdAt;
            response["output"] = outputItems.Cast<object?>().ToList();
            response["usage"] = usage;
        }

        responseAccumulator.Accept(new SseEvent(eventType, new Dictionary<string, object?>
        {
            ["type"] = eventType,
            ["response"] = response
        }));
        return responseAccumulator.BuildResponse()!;
    }

    private static object? NormalizeResponsesStreamJsonValue(object? value)
    {
        if (value is Dictionary<string, object?> dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, item) in dictionary)
            {
                result[key] = NormalizeResponsesStreamJsonValue(item);
            }
            return result;
        }

        if (value is List<object?> list)
        {
            return list.Select(NormalizeResponsesStreamJsonValue).ToList();
        }

        return value;
    }

    private static string MissingSuffix(string emitted, string complete)
        => complete.StartsWith(emitted, StringComparison.Ordinal) ? complete[emitted.Length..] : complete;

    private static List<object?> BuildResponsesMessageContent(
        string text,
        IReadOnlyCollection<string> refusalParts,
        IReadOnlyCollection<object?> annotations)
    {
        var content = new List<object?>();
        if (text.Length > 0)
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "output_text",
                ["text"] = text,
                ["annotations"] = annotations.ToList()
            });
        }

        if (refusalParts.Count > 0)
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "refusal",
                ["refusal"] = string.Concat(refusalParts)
            });
        }

        return content;
    }

    private static bool IsNativeMcpItemType(string itemType)
        => itemType is "mcp_call" or "mcp_list_tools" or "mcp_approval_request" or "mcp_approval_response";

    private static bool IsResponsesNativeToolCallType(string itemType)
        => itemType.EndsWith("_call", StringComparison.Ordinal)
            || itemType is "mcp_list_tools" or "mcp_approval_request" or "mcp_approval_response";

    private static bool IsServerExecutedNativeToolCallType(string itemType)
        => itemType is "web_search_call" or "file_search_call" or "code_interpreter_call" or "image_generation_call";

    private static bool IsServerExecutedToolSearchCall(Dictionary<string, object?> item)
        => string.Equals(StringValue(item, "type", string.Empty), "tool_search_call", StringComparison.Ordinal)
            && string.Equals(StringValue(item, "execution", string.Empty), "server", StringComparison.Ordinal);

    private static bool TryGetClientToolCallInfo(
        Dictionary<string, object?> item,
        out string name,
        out ResponsesToolCallKind callKind,
        out string argumentField)
    {
        var itemType = StringValue(item, "type", string.Empty);
        name = StringValue(item, "name", string.Empty);
        argumentField = "arguments";
        callKind = ProtocolConverter.GetResponsesToolCallKind(name);
        if (itemType == "function_call")
        {
            return name.Length > 0;
        }

        if (itemType == "custom_tool_call")
        {
            callKind = ResponsesToolCallKind.CustomTool;
            argumentField = "input";
            return name.Length > 0;
        }

        var defaultName = itemType switch
        {
            "tool_search_call" => "tool_search",
            "apply_patch_call" => "apply_patch",
            "local_shell_call" => "local_shell",
            "shell_call" => "shell",
            "computer_call" or "computer_use_call" => "computer_use",
            _ => string.Empty
        };
        if (defaultName.Length == 0)
        {
            return false;
        }

        name = name.Length > 0 ? name : defaultName;
        argumentField = itemType == "tool_search_call" ? "arguments" : "input";
        callKind = itemType == "apply_patch_call"
            ? ResponsesToolCallKind.CustomTool
            : ResponsesToolCallKind.NativeTool;
        return itemType != "tool_search_call"
            || !item.TryGetValue("execution", out var execution)
            || string.Equals(execution?.ToString(), "client", StringComparison.Ordinal);
    }

    private static bool TryGetToolArguments(Dictionary<string, object?> item, out string argumentsJson)
    {
        argumentsJson = string.Empty;
        foreach (var key in new[] { "arguments", "input", "action" })
        {
            if (!item.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            argumentsJson = value is string text && LooksLikeJson(text)
                ? text
                : JsonSerializer.Serialize(NormalizeResponsesStreamJsonValue(value), JsonOptions);
            return true;
        }

        return false;
    }

    private static bool LooksLikeJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

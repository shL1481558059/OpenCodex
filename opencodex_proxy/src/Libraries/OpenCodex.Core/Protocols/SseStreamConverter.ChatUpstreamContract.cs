namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    private static Dictionary<string, object?> ChatUsageToResponsesUsage(
        IReadOnlyDictionary<string, object?> usage)
    {
        var inputTokens = ToInt(GetValue(usage, "prompt_tokens"));
        var outputTokens = ToInt(GetValue(usage, "completion_tokens"));
        var responseUsage = new Dictionary<string, object?>
        {
            ["input_tokens"] = inputTokens,
            ["output_tokens"] = outputTokens,
            ["total_tokens"] = inputTokens + outputTokens
        };

        if (TryAsObject(GetValue(usage, "prompt_tokens_details"), out var promptDetails)
            && promptDetails.Count > 0)
        {
            responseUsage["input_tokens_details"] = new Dictionary<string, object?>
            {
                ["cached_tokens"] = ToInt(GetValue(promptDetails, "cached_tokens"))
            };
        }
        else if (TryAsObject(GetValue(usage, "input_tokens_details"), out var inputDetails)
                 && inputDetails.Count > 0)
        {
            responseUsage["input_tokens_details"] = inputDetails;
        }

        if (TryAsObject(GetValue(usage, "completion_tokens_details"), out var completionDetails)
            && completionDetails.Count > 0)
        {
            var outputDetails = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (GetValue(completionDetails, "reasoning_tokens") is not null)
            {
                outputDetails["reasoning_tokens"] = ToInt(GetValue(completionDetails, "reasoning_tokens"));
            }

            if (outputDetails.Count > 0)
            {
                responseUsage["output_tokens_details"] = outputDetails;
            }
        }

        return responseUsage;
    }

    private static Dictionary<string, object?> BuildEmptyChatCompletion(
        string? model,
        long createdAt,
        string finishReason,
        Dictionary<string, object?> usage)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = $"chatcmpl_{Guid.NewGuid():N}",
            ["object"] = "chat.completion",
            ["created"] = createdAt,
            ["model"] = model,
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = string.Empty,
                        ["tool_calls"] = new List<object?>()
                    },
                    ["finish_reason"] = finishReason
                }
            },
            ["usage"] = usage
        };
    }
}

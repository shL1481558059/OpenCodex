using static OpenCodex.Api.Abstractions.WebSearchPayload;

namespace OpenCodex.Api.Services.WebSearch;

internal static class WebSearchSimulationLog
{
    public static Dictionary<string, object?> Build(
        IReadOnlyList<WebSearchToolResult> results,
        IReadOnlyList<Dictionary<string, object?>> upstreamCalls)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["calls"] = results
                .Select(result => (object?)new Dictionary<string, object?>
                {
                    ["call_id"] = result.CallId,
                    ["query"] = result.Query,
                    ["status"] = result.Status,
                    ["error"] = result.LogError,
                    ["error_type"] = result.ErrorType,
                    ["http_status"] = result.HttpStatus,
                    ["provider"] = result.Provider,
                    ["key_id"] = result.KeyId,
                    ["key_position"] = result.KeyPosition,
                    ["key_usage_count"] = result.KeyUsageCount,
                    ["key_usage_limit"] = result.KeyUsageLimit,
                    ["raw"] = result.Raw
                })
                .ToList(),
            ["upstream_calls"] = upstreamCalls.Select(call => (object?)DeepCopyObject(call)).ToList(),
            ["upstream_call_summary"] = upstreamCalls
                .Select(call =>
                {
                    var toolCalls = ListValue(call, "tool_calls");
                    var toolNames = new List<object?>();
                    foreach (var item in toolCalls)
                    {
                        if (TryAsObject(item, out var toolCall))
                        {
                            toolNames.Add(StringValue(toolCall, "name"));
                        }
                    }

                    return (object?)new Dictionary<string, object?>
                    {
                        ["iteration"] = GetValue(call, "iteration"),
                        ["after_limit"] = GetValue(call, "after_limit") is true,
                        ["tool_call_count"] = toolCalls.Count,
                        ["tool_names"] = toolNames
                    };
                })
                .ToList()
        };
    }
}

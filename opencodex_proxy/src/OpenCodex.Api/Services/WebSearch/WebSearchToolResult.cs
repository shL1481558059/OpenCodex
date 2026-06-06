using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Services.WebSearch;

internal sealed record WebSearchToolResult(
    string CallId,
    string Query,
    string Status,
    string ToolResult,
    Dictionary<string, object?> OpenCodexResult,
    string? LogError,
    string? Provider,
    long? KeyId,
    int? KeyPosition,
    int? KeyUsageCount,
    int? KeyUsageLimit,
    string? ErrorType,
    int? HttpStatus,
    object? Raw)
{
    public static WebSearchToolResult Failed(string callId, string query, string error)
    {
        var resultPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["answer"] = string.Empty,
            ["results"] = new List<object?>(),
            ["error"] = error
        };
        return new WebSearchToolResult(
            callId,
            query,
            "failed",
            WebSearchPayload.JsonDumps(resultPayload),
            resultPayload,
            error,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    public static WebSearchToolResult FromProvider(
        string callId,
        string query,
        TavilyKeyRecord key,
        WebSearchProviderResult result)
    {
        var resultPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["answer"] = result.Summary.Answer,
            ["results"] = result.Summary.Results.Select(item => (object?)item).ToList(),
            ["error"] = result.Ok ? null : "搜索不可用"
        };
        return new WebSearchToolResult(
            callId,
            query,
            result.Ok ? "completed" : "failed",
            WebSearchPayload.JsonDumps(resultPayload),
            resultPayload,
            result.Summary.Error ?? result.Error,
            key.Provider,
            key.Id,
            key.Position,
            key.UsageCount,
            key.KeyUsageLimit,
            result.ErrorType,
            result.StatusCode,
            result.Raw);
    }
}

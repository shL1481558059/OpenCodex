using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.Core.Services.WebSearch;

internal sealed class WebSearchToolResult
{
    public WebSearchToolResult(
        string callId,
        string query,
        string status,
        string toolResult,
        Dictionary<string, object?> openCodexResult,
        string? logError,
        string? provider,
        long? keyId,
        int? keyPosition,
        int? keyUsageCount,
        int? keyUsageLimit,
        string? errorType,
        int? httpStatus,
        object? raw)
    {
        CallId = callId;
        Query = query;
        Status = status;
        ToolResult = toolResult;
        OpenCodexResult = openCodexResult;
        LogError = logError;
        Provider = provider;
        KeyId = keyId;
        KeyPosition = keyPosition;
        KeyUsageCount = keyUsageCount;
        KeyUsageLimit = keyUsageLimit;
        ErrorType = errorType;
        HttpStatus = httpStatus;
        Raw = raw;
    }

    public string CallId { get; }

    public string Query { get; }

    public string Status { get; }

    public string ToolResult { get; }

    public Dictionary<string, object?> OpenCodexResult { get; }

    public string? LogError { get; }

    public string? Provider { get; }

    public long? KeyId { get; }

    public int? KeyPosition { get; }

    public int? KeyUsageCount { get; }

    public int? KeyUsageLimit { get; }

    public string? ErrorType { get; }

    public int? HttpStatus { get; }

    public object? Raw { get; }

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
        TavilyKeyDto key,
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

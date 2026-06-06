using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public sealed class ProxyLogRepository : IProxyLogRepository
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public ProxyLogRepository(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public UsageRecord ExtractUsage(
        IReadOnlyDictionary<string, object?> response,
        string protocol)
    {
        return OpenCodexDatabase.ExtractUsage(response, protocol);
    }

    public double CalculateCost(
        string model,
        int inputTokens,
        int cachedTokens,
        int outputTokens)
    {
        return OpenCodexDatabase.CalculateCost(model, inputTokens, cachedTokens, outputTokens);
    }

    public long WriteRequestLog(RequestLogWriteRecord record)
    {
        var settings = _settingsProvider.GetSettings();
        return OpenCodexDatabase.WriteRequestLog(
            settings.DbPath,
            ToDatabaseRecord(record),
            settings.AdminUsername);
    }

    private static Dictionary<string, object?> ToDatabaseRecord(RequestLogWriteRecord record)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["request_id"] = record.RequestId,
            ["created_at"] = record.CreatedAt,
            ["method"] = record.Method,
            ["path"] = record.Path,
            ["client_ip"] = record.ClientIp,
            ["request_headers"] = record.RequestHeaders,
            ["request_body"] = record.RequestBody,
            ["upstream_request_body"] = record.UpstreamRequestBody,
            ["upstream_response_body"] = record.UpstreamResponseBody,
            ["response_body"] = record.ResponseBody,
            ["web_search_json"] = record.WebSearchJson,
            ["model"] = record.Model,
            ["upstream_model"] = record.UpstreamModel,
            ["channel_id"] = record.ChannelId,
            ["is_stream"] = record.IsStream ? 1 : 0,
            ["ttft_ms"] = record.TtftMs,
            ["duration_ms"] = record.DurationMs,
            ["status_code"] = record.StatusCode,
            ["input_tokens"] = record.InputTokens,
            ["cached_tokens"] = record.CachedTokens,
            ["output_tokens"] = record.OutputTokens,
            ["cost"] = record.Cost,
            ["owner_username"] = record.OwnerUsername,
            ["api_key_id"] = record.ApiKeyId,
            ["error"] = record.Error
        };
    }
}

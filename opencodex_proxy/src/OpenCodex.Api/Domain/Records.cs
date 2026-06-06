namespace OpenCodex.Api.Domain;

public sealed record ChannelRecord(
    string OwnerUsername,
    string Id,
    string Name,
    string Type,
    string BaseUrl,
    string ApiKey,
    string AuthMode,
    IReadOnlyDictionary<string, object?> Headers,
    int TimeoutSeconds,
    int RetryCount,
    IReadOnlyDictionary<string, object?> Compat,
    IReadOnlyList<object?> Models,
    bool Enabled);

public sealed record UserRecord(
    string Username,
    string Role,
    bool Enabled,
    double CreatedAt,
    double UpdatedAt);

public sealed record AccessApiKeyRecord(
    long Id,
    string OwnerUsername,
    string Name,
    string KeyPrefix,
    string KeySuffix,
    string MaskedKey,
    bool Enabled,
    double CreatedAt,
    double UpdatedAt,
    double? LastUsedAt,
    string? Key);

public sealed record AccessApiKeyUserRecord(
    string Username,
    string Role,
    bool Enabled);

public sealed record AuthenticatedAccessApiKeyRecord(
    long Id,
    string OwnerUsername,
    string Name,
    string KeyPrefix,
    string KeySuffix,
    string MaskedKey,
    bool Enabled,
    double CreatedAt,
    double UpdatedAt,
    double? LastUsedAt,
    AccessApiKeyUserRecord User);

public sealed record TavilyKeyRecord(
    long Id,
    int Position,
    string Provider,
    string Key,
    bool Enabled,
    int UsageCount,
    int UsageLimit,
    int KeyUsageLimit);

public sealed record WebSearchConfigRecord(
    bool Enabled,
    IReadOnlyList<string> Providers,
    int DefaultKeyUsageLimit,
    IReadOnlyList<TavilyKeyRecord> Keys);

public sealed record UsageRecord(
    int InputTokens,
    int CachedTokens,
    int OutputTokens);

public sealed record RequestLogWriteRecord(
    string RequestId,
    double CreatedAt,
    string Method,
    string Path,
    string? ClientIp,
    string RequestHeaders,
    string RequestBody,
    string UpstreamRequestBody,
    string UpstreamResponseBody,
    string ResponseBody,
    string? WebSearchJson,
    string? Model,
    string? UpstreamModel,
    string? ChannelId,
    bool IsStream,
    int? TtftMs,
    int DurationMs,
    int StatusCode,
    int InputTokens,
    int CachedTokens,
    int OutputTokens,
    double Cost,
    string OwnerUsername,
    long? ApiKeyId,
    string? Error);

public sealed record RequestLogRecord(
    long Id,
    string? RequestId,
    double? CreatedAt,
    string? Method,
    string? Path,
    string? ClientIp,
    string? Model,
    string? UpstreamModel,
    string? ChannelId,
    bool IsStream,
    int? TtftMs,
    int? DurationMs,
    int? StatusCode,
    int InputTokens,
    int CachedTokens,
    int OutputTokens,
    double Cost,
    string OwnerUsername,
    long? ApiKeyId,
    string? Error,
    string? RequestHeaders,
    string? RequestBody,
    string? UpstreamRequestBody,
    string? UpstreamResponseBody,
    string? ResponseBody,
    string? WebSearchJson,
    string RequestStatus);

public sealed record RequestLogEventRecord(
    long Id,
    string? RequestId,
    double? CreatedAt,
    string? Method,
    string? Path,
    string? ClientIp,
    string? Model,
    string? UpstreamModel,
    string? ChannelId,
    bool IsStream,
    int? TtftMs,
    int? DurationMs,
    int? StatusCode,
    int InputTokens,
    int CachedTokens,
    int OutputTokens,
    double Cost,
    string OwnerUsername,
    long? ApiKeyId,
    string? Error,
    string RequestStatus);

public sealed record RequestLogPageRecord(
    IReadOnlyList<RequestLogEventRecord> Events,
    int Total,
    int Page,
    int PageSize);

public sealed record StatsPointRecord(
    string Time,
    double Cost,
    int InputTokens,
    int CachedTokens,
    int OutputTokens,
    double? AvgTtftMs,
    double? CacheHitRate,
    double Rpm);

public sealed record StatsSummaryRecord(
    int RequestCount,
    int SuccessCount,
    int Recent1hRequestCount,
    int InputTokens,
    int CachedTokens,
    int OutputTokens,
    int TotalTokens,
    int Recent1hTokens,
    double Cost,
    double Recent1hCost,
    double Rpm,
    double Tpm);

public sealed record ModelDistributionRecord(
    string Model,
    int Count);

public sealed record StatsRecord(
    string Range,
    string Start,
    string End,
    int GranularityMinutes,
    double CurrencyRate,
    StatsSummaryRecord Summary,
    IReadOnlyList<StatsPointRecord> Points,
    IReadOnlyList<ModelDistributionRecord> ModelDistribution);

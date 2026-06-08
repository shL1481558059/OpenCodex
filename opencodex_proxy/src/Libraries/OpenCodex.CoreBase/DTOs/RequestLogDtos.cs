namespace OpenCodex.CoreBase.DTOs;

public sealed class RequestLogWriteDto(
    string requestId,
    double createdAt,
    string method,
    string path,
    string? clientIp,
    string requestHeaders,
    string requestBody,
    string upstreamRequestBody,
    string upstreamResponseBody,
    string responseBody,
    string? webSearchJson,
    string? model,
    string? upstreamModel,
    string? channelId,
    bool isStream,
    int? ttftMs,
    int durationMs,
    int statusCode,
    int inputTokens,
    int cachedTokens,
    int outputTokens,
    double cost,
    string ownerUsername,
    long? apiKeyId,
    string? error)
{
    public string RequestId { get; } = requestId;

    public double CreatedAt { get; } = createdAt;

    public string Method { get; } = method;

    public string Path { get; } = path;

    public string? ClientIp { get; } = clientIp;

    public string RequestHeaders { get; } = requestHeaders;

    public string RequestBody { get; } = requestBody;

    public string UpstreamRequestBody { get; } = upstreamRequestBody;

    public string UpstreamResponseBody { get; } = upstreamResponseBody;

    public string ResponseBody { get; } = responseBody;

    public string? WebSearchJson { get; } = webSearchJson;

    public string? Model { get; } = model;

    public string? UpstreamModel { get; } = upstreamModel;

    public string? ChannelId { get; } = channelId;

    public bool IsStream { get; } = isStream;

    public int? TtftMs { get; } = ttftMs;

    public int DurationMs { get; } = durationMs;

    public int StatusCode { get; } = statusCode;

    public int InputTokens { get; } = inputTokens;

    public int CachedTokens { get; } = cachedTokens;

    public int OutputTokens { get; } = outputTokens;

    public double Cost { get; } = cost;

    public string OwnerUsername { get; } = ownerUsername;

    public long? ApiKeyId { get; } = apiKeyId;

    public string? Error { get; } = error;
}

public sealed class RequestLogDto(
    long id,
    string? requestId,
    double? createdAt,
    string? method,
    string? path,
    string? clientIp,
    string? model,
    string? upstreamModel,
    string? channelId,
    bool isStream,
    int? ttftMs,
    int? durationMs,
    int? statusCode,
    int inputTokens,
    int cachedTokens,
    int outputTokens,
    double cost,
    string ownerUsername,
    long? apiKeyId,
    string? error,
    string? requestHeaders,
    string? requestBody,
    string? upstreamRequestBody,
    string? upstreamResponseBody,
    string? responseBody,
    string? webSearchJson,
    string requestStatus)
{
    public long Id { get; } = id;

    public string? RequestId { get; } = requestId;

    public double? CreatedAt { get; } = createdAt;

    public string? Method { get; } = method;

    public string? Path { get; } = path;

    public string? ClientIp { get; } = clientIp;

    public string? Model { get; } = model;

    public string? UpstreamModel { get; } = upstreamModel;

    public string? ChannelId { get; } = channelId;

    public bool IsStream { get; } = isStream;

    public int? TtftMs { get; } = ttftMs;

    public int? DurationMs { get; } = durationMs;

    public int? StatusCode { get; } = statusCode;

    public int InputTokens { get; } = inputTokens;

    public int CachedTokens { get; } = cachedTokens;

    public int OutputTokens { get; } = outputTokens;

    public double Cost { get; } = cost;

    public string OwnerUsername { get; } = ownerUsername;

    public long? ApiKeyId { get; } = apiKeyId;

    public string? Error { get; } = error;

    public string? RequestHeaders { get; } = requestHeaders;

    public string? RequestBody { get; } = requestBody;

    public string? UpstreamRequestBody { get; } = upstreamRequestBody;

    public string? UpstreamResponseBody { get; } = upstreamResponseBody;

    public string? ResponseBody { get; } = responseBody;

    public string? WebSearchJson { get; } = webSearchJson;

    public string RequestStatus { get; } = requestStatus;
}

public sealed class RequestLogEventDto(
    long id,
    string? requestId,
    double? createdAt,
    string? method,
    string? path,
    string? clientIp,
    string? model,
    string? upstreamModel,
    string? channelId,
    bool isStream,
    int? ttftMs,
    int? durationMs,
    int? statusCode,
    int inputTokens,
    int cachedTokens,
    int outputTokens,
    double cost,
    string ownerUsername,
    long? apiKeyId,
    string? error,
    string requestStatus)
{
    public long Id { get; } = id;

    public string? RequestId { get; } = requestId;

    public double? CreatedAt { get; } = createdAt;

    public string? Method { get; } = method;

    public string? Path { get; } = path;

    public string? ClientIp { get; } = clientIp;

    public string? Model { get; } = model;

    public string? UpstreamModel { get; } = upstreamModel;

    public string? ChannelId { get; } = channelId;

    public bool IsStream { get; } = isStream;

    public int? TtftMs { get; } = ttftMs;

    public int? DurationMs { get; } = durationMs;

    public int? StatusCode { get; } = statusCode;

    public int InputTokens { get; } = inputTokens;

    public int CachedTokens { get; } = cachedTokens;

    public int OutputTokens { get; } = outputTokens;

    public double Cost { get; } = cost;

    public string OwnerUsername { get; } = ownerUsername;

    public long? ApiKeyId { get; } = apiKeyId;

    public string? Error { get; } = error;

    public string RequestStatus { get; } = requestStatus;
}

public sealed class RequestLogPageDto(
    IReadOnlyList<RequestLogEventDto> events,
    int total,
    int page,
    int pageSize)
{
    public IReadOnlyList<RequestLogEventDto> Events { get; } = events;

    public int Total { get; } = total;

    public int Page { get; } = page;

    public int PageSize { get; } = pageSize;
}

public sealed class UsageDto(
    int inputTokens,
    int cachedTokens,
    int outputTokens)
{
    public int InputTokens { get; } = inputTokens;

    public int CachedTokens { get; } = cachedTokens;

    public int OutputTokens { get; } = outputTokens;
}

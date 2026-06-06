using System.Text.Json.Serialization;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.DTOs.AdminObservability;

public sealed class LogsPageResponse
{
    public LogsPageResponse(IReadOnlyList<LogEventResponse> events, int total, int page, int pageSize)
    {
        Events = events;
        Total = total;
        Page = page;
        PageSize = pageSize;
    }

    [JsonPropertyName("events")]
    public IReadOnlyList<LogEventResponse> Events { get; }

    [JsonPropertyName("total")]
    public int Total { get; }

    [JsonPropertyName("page")]
    public int Page { get; }

    [JsonPropertyName("page_size")]
    public int PageSize { get; }

    public static LogsPageResponse From(RequestLogPageRecord page)
    {
        return new LogsPageResponse(
            page.Events.Select(LogEventResponse.From).ToList(),
            page.Total,
            page.Page,
            page.PageSize);
    }
}

public sealed class LogEventResponse
{
    public LogEventResponse(
        long id,
        string? requestId,
        double? createdAt,
        string? method,
        string? path,
        string? clientIp,
        string? model,
        string? upstreamModel,
        string? channelId,
        int isStream,
        int? ttftMs,
        int? durationMs,
        int? statusCode,
        int inputTokens,
        int cachedTokens,
        int outputTokens,
        double cost,
        string? ownerUsername,
        long? apiKeyId,
        string? error,
        string requestStatus)
    {
        Id = id;
        RequestId = requestId;
        CreatedAt = createdAt;
        Method = method;
        Path = path;
        ClientIp = clientIp;
        Model = model;
        UpstreamModel = upstreamModel;
        ChannelId = channelId;
        IsStream = isStream;
        TtftMs = ttftMs;
        DurationMs = durationMs;
        StatusCode = statusCode;
        InputTokens = inputTokens;
        CachedTokens = cachedTokens;
        OutputTokens = outputTokens;
        Cost = cost;
        OwnerUsername = ownerUsername;
        ApiKeyId = apiKeyId;
        Error = error;
        RequestStatus = requestStatus;
    }

    [JsonPropertyName("id")]
    public long Id { get; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; }

    [JsonPropertyName("created_at")]
    public double? CreatedAt { get; }

    [JsonPropertyName("method")]
    public string? Method { get; }

    [JsonPropertyName("path")]
    public string? Path { get; }

    [JsonPropertyName("client_ip")]
    public string? ClientIp { get; }

    [JsonPropertyName("model")]
    public string? Model { get; }

    [JsonPropertyName("upstream_model")]
    public string? UpstreamModel { get; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; }

    [JsonPropertyName("is_stream")]
    public int IsStream { get; }

    [JsonPropertyName("ttft_ms")]
    public int? TtftMs { get; }

    [JsonPropertyName("duration_ms")]
    public int? DurationMs { get; }

    [JsonPropertyName("status_code")]
    public int? StatusCode { get; }

    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; }

    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; }

    [JsonPropertyName("cost")]
    public double Cost { get; }

    [JsonPropertyName("owner_username")]
    public string? OwnerUsername { get; }

    [JsonPropertyName("api_key_id")]
    public long? ApiKeyId { get; }

    [JsonPropertyName("error")]
    public string? Error { get; }

    [JsonPropertyName("request_status")]
    public string RequestStatus { get; }

    public static LogEventResponse From(RequestLogEventRecord log)
    {
        return new LogEventResponse(
            log.Id,
            log.RequestId,
            log.CreatedAt,
            log.Method,
            log.Path,
            log.ClientIp,
            log.Model,
            log.UpstreamModel,
            log.ChannelId,
            log.IsStream ? 1 : 0,
            log.TtftMs,
            log.DurationMs,
            log.StatusCode,
            log.InputTokens,
            log.CachedTokens,
            log.OutputTokens,
            log.Cost,
            log.OwnerUsername,
            log.ApiKeyId,
            log.Error,
            log.RequestStatus);
    }
}

public sealed class LogDetailResponse
{
    public LogDetailResponse(
        long id,
        string? requestId,
        double? createdAt,
        string? method,
        string? path,
        string? clientIp,
        string? model,
        string? upstreamModel,
        string? channelId,
        int isStream,
        int? ttftMs,
        int? durationMs,
        int? statusCode,
        int inputTokens,
        int cachedTokens,
        int outputTokens,
        double cost,
        string? ownerUsername,
        long? apiKeyId,
        string? error,
        string requestStatus,
        string? requestHeaders,
        string? requestBody,
        string? upstreamRequestBody,
        string? upstreamResponseBody,
        string? responseBody,
        string? webSearchJson)
    {
        Id = id;
        RequestId = requestId;
        CreatedAt = createdAt;
        Method = method;
        Path = path;
        ClientIp = clientIp;
        Model = model;
        UpstreamModel = upstreamModel;
        ChannelId = channelId;
        IsStream = isStream;
        TtftMs = ttftMs;
        DurationMs = durationMs;
        StatusCode = statusCode;
        InputTokens = inputTokens;
        CachedTokens = cachedTokens;
        OutputTokens = outputTokens;
        Cost = cost;
        OwnerUsername = ownerUsername;
        ApiKeyId = apiKeyId;
        Error = error;
        RequestStatus = requestStatus;
        RequestHeaders = requestHeaders;
        RequestBody = requestBody;
        UpstreamRequestBody = upstreamRequestBody;
        UpstreamResponseBody = upstreamResponseBody;
        ResponseBody = responseBody;
        WebSearchJson = webSearchJson;
    }

    [JsonPropertyName("id")]
    public long Id { get; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; }

    [JsonPropertyName("created_at")]
    public double? CreatedAt { get; }

    [JsonPropertyName("method")]
    public string? Method { get; }

    [JsonPropertyName("path")]
    public string? Path { get; }

    [JsonPropertyName("client_ip")]
    public string? ClientIp { get; }

    [JsonPropertyName("model")]
    public string? Model { get; }

    [JsonPropertyName("upstream_model")]
    public string? UpstreamModel { get; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; }

    [JsonPropertyName("is_stream")]
    public int IsStream { get; }

    [JsonPropertyName("ttft_ms")]
    public int? TtftMs { get; }

    [JsonPropertyName("duration_ms")]
    public int? DurationMs { get; }

    [JsonPropertyName("status_code")]
    public int? StatusCode { get; }

    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; }

    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; }

    [JsonPropertyName("cost")]
    public double Cost { get; }

    [JsonPropertyName("owner_username")]
    public string? OwnerUsername { get; }

    [JsonPropertyName("api_key_id")]
    public long? ApiKeyId { get; }

    [JsonPropertyName("error")]
    public string? Error { get; }

    [JsonPropertyName("request_status")]
    public string RequestStatus { get; }

    [JsonPropertyName("request_headers")]
    public string? RequestHeaders { get; }

    [JsonPropertyName("request_body")]
    public string? RequestBody { get; }

    [JsonPropertyName("upstream_request_body")]
    public string? UpstreamRequestBody { get; }

    [JsonPropertyName("upstream_response_body")]
    public string? UpstreamResponseBody { get; }

    [JsonPropertyName("response_body")]
    public string? ResponseBody { get; }

    [JsonPropertyName("web_search_json")]
    public string? WebSearchJson { get; }

    public static LogDetailResponse From(RequestLogRecord log)
    {
        var logEvent = LogEventResponse.From(new RequestLogEventRecord(
            log.Id,
            log.RequestId,
            log.CreatedAt,
            log.Method,
            log.Path,
            log.ClientIp,
            log.Model,
            log.UpstreamModel,
            log.ChannelId,
            log.IsStream,
            log.TtftMs,
            log.DurationMs,
            log.StatusCode,
            log.InputTokens,
            log.CachedTokens,
            log.OutputTokens,
            log.Cost,
            log.OwnerUsername,
            log.ApiKeyId,
            log.Error,
            log.RequestStatus));

        return new LogDetailResponse(
            logEvent.Id,
            logEvent.RequestId,
            logEvent.CreatedAt,
            logEvent.Method,
            logEvent.Path,
            logEvent.ClientIp,
            logEvent.Model,
            logEvent.UpstreamModel,
            logEvent.ChannelId,
            logEvent.IsStream,
            logEvent.TtftMs,
            logEvent.DurationMs,
            logEvent.StatusCode,
            logEvent.InputTokens,
            logEvent.CachedTokens,
            logEvent.OutputTokens,
            logEvent.Cost,
            logEvent.OwnerUsername,
            logEvent.ApiKeyId,
            logEvent.Error,
            logEvent.RequestStatus,
            log.RequestHeaders,
            log.RequestBody,
            log.UpstreamRequestBody,
            log.UpstreamResponseBody,
            log.ResponseBody,
            log.WebSearchJson);
    }
}

public sealed class StatsResponse
{
    public StatsResponse(
        string range,
        string start,
        string end,
        int granularityMinutes,
        double currencyRate,
        StatsSummaryResponse summary,
        IReadOnlyList<StatsPointResponse> points,
        IReadOnlyList<StatsModelDistributionResponse> modelDistribution)
    {
        Range = range;
        Start = start;
        End = end;
        GranularityMinutes = granularityMinutes;
        CurrencyRate = currencyRate;
        Summary = summary;
        Points = points;
        ModelDistribution = modelDistribution;
    }

    [JsonPropertyName("range")]
    public string Range { get; }

    [JsonPropertyName("start")]
    public string Start { get; }

    [JsonPropertyName("end")]
    public string End { get; }

    [JsonPropertyName("granularity_minutes")]
    public int GranularityMinutes { get; }

    [JsonPropertyName("currency_rate")]
    public double CurrencyRate { get; }

    [JsonPropertyName("summary")]
    public StatsSummaryResponse Summary { get; }

    [JsonPropertyName("points")]
    public IReadOnlyList<StatsPointResponse> Points { get; }

    [JsonPropertyName("model_distribution")]
    public IReadOnlyList<StatsModelDistributionResponse> ModelDistribution { get; }

    public static StatsResponse From(StatsRecord stats)
    {
        return new StatsResponse(
            stats.Range,
            stats.Start,
            stats.End,
            stats.GranularityMinutes,
            stats.CurrencyRate,
            StatsSummaryResponse.From(stats.Summary),
            stats.Points.Select(StatsPointResponse.From).ToList(),
            stats.ModelDistribution.Select(StatsModelDistributionResponse.From).ToList());
    }
}

public sealed class StatsSummaryResponse
{
    public StatsSummaryResponse(
        int requestCount,
        int successCount,
        int recent1hRequestCount,
        int inputTokens,
        int cachedTokens,
        int outputTokens,
        int totalTokens,
        int recent1hTokens,
        double cost,
        double recent1hCost,
        double rpm,
        double tpm)
    {
        RequestCount = requestCount;
        SuccessCount = successCount;
        Recent1hRequestCount = recent1hRequestCount;
        InputTokens = inputTokens;
        CachedTokens = cachedTokens;
        OutputTokens = outputTokens;
        TotalTokens = totalTokens;
        Recent1hTokens = recent1hTokens;
        Cost = cost;
        Recent1hCost = recent1hCost;
        Rpm = rpm;
        Tpm = tpm;
    }

    [JsonPropertyName("request_count")]
    public int RequestCount { get; }

    [JsonPropertyName("success_count")]
    public int SuccessCount { get; }

    [JsonPropertyName("recent_1h_request_count")]
    public int Recent1hRequestCount { get; }

    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; }

    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; }

    [JsonPropertyName("recent_1h_tokens")]
    public int Recent1hTokens { get; }

    [JsonPropertyName("cost")]
    public double Cost { get; }

    [JsonPropertyName("recent_1h_cost")]
    public double Recent1hCost { get; }

    [JsonPropertyName("rpm")]
    public double Rpm { get; }

    [JsonPropertyName("tpm")]
    public double Tpm { get; }

    public static StatsSummaryResponse From(StatsSummaryRecord summary)
    {
        return new StatsSummaryResponse(
            summary.RequestCount,
            summary.SuccessCount,
            summary.Recent1hRequestCount,
            summary.InputTokens,
            summary.CachedTokens,
            summary.OutputTokens,
            summary.TotalTokens,
            summary.Recent1hTokens,
            summary.Cost,
            summary.Recent1hCost,
            summary.Rpm,
            summary.Tpm);
    }
}

public sealed class StatsPointResponse
{
    public StatsPointResponse(
        string time,
        double cost,
        int inputTokens,
        int cachedTokens,
        int outputTokens,
        double? avgTtftMs,
        double? cacheHitRate,
        double rpm)
    {
        Time = time;
        Cost = cost;
        InputTokens = inputTokens;
        CachedTokens = cachedTokens;
        OutputTokens = outputTokens;
        AvgTtftMs = avgTtftMs;
        CacheHitRate = cacheHitRate;
        Rpm = rpm;
    }

    [JsonPropertyName("time")]
    public string Time { get; }

    [JsonPropertyName("cost")]
    public double Cost { get; }

    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; }

    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; }

    [JsonPropertyName("avg_ttft_ms")]
    public double? AvgTtftMs { get; }

    [JsonPropertyName("cache_hit_rate")]
    public double? CacheHitRate { get; }

    [JsonPropertyName("rpm")]
    public double Rpm { get; }

    public static StatsPointResponse From(StatsPointRecord point)
    {
        return new StatsPointResponse(
            point.Time,
            point.Cost,
            point.InputTokens,
            point.CachedTokens,
            point.OutputTokens,
            point.AvgTtftMs,
            point.CacheHitRate,
            point.Rpm);
    }
}

public sealed class StatsModelDistributionResponse
{
    public StatsModelDistributionResponse(string model, int count)
    {
        Model = model;
        Count = count;
    }

    [JsonPropertyName("model")]
    public string Model { get; }

    [JsonPropertyName("count")]
    public int Count { get; }

    public static StatsModelDistributionResponse From(ModelDistributionRecord item)
    {
        return new StatsModelDistributionResponse(item.Model, item.Count);
    }
}

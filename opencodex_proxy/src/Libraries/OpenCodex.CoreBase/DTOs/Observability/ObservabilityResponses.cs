using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.DTOs.Observability;

/// <summary>
/// 表示请求日志分页响应。
/// </summary>
public sealed class LogsPageResponse
{
    /// <summary>
    /// 初始化请求日志分页响应。
    /// </summary>
    /// <param name="events">日志事件列表。</param>
    /// <param name="total">符合条件的日志总数。</param>
    /// <param name="page">当前页码。</param>
    /// <param name="pageSize">每页数量。</param>
    public LogsPageResponse(IReadOnlyList<LogEventResponse> events, int total, int page, int pageSize)
    {
        Events = events;
        Total = total;
        Page = page;
        PageSize = pageSize;
    }

    /// <summary>
    /// 获取日志事件列表。
    /// </summary>
    [JsonPropertyName("events")]
    public IReadOnlyList<LogEventResponse> Events { get; }

    /// <summary>
    /// 获取符合条件的日志总数。
    /// </summary>
    [JsonPropertyName("total")]
    public int Total { get; }

    /// <summary>
    /// 获取当前页码。
    /// </summary>
    [JsonPropertyName("page")]
    public int Page { get; }

    /// <summary>
    /// 获取每页数量。
    /// </summary>
    [JsonPropertyName("page_size")]
    public int PageSize { get; }

    /// <summary>
    /// 根据日志分页数据创建响应对象。
    /// </summary>
    /// <param name="page">日志分页数据。</param>
    /// <returns>请求日志分页响应。</returns>
    public static LogsPageResponse From(
        RequestLogPageDto page,
        IReadOnlyDictionary<long, string>? apiKeyNames = null)
    {
        return new LogsPageResponse(
            page.Events.Select(log => LogEventResponse.From(log, apiKeyNames)).ToList(),
            page.Total,
            page.Page,
            page.PageSize);
    }
}

/// <summary>
/// 表示请求日志筛选中的访问密钥选项。
/// </summary>
public sealed class LogApiKeyFilterOption
{
    /// <summary>
    /// 初始化 <see cref="LogApiKeyFilterOption"/> 类的新实例。
    /// </summary>
    /// <param name="id">访问密钥标识。</param>
    /// <param name="name">访问密钥显示名称。</param>
    public LogApiKeyFilterOption(long id, string? name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>
    /// 获取访问密钥标识。
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; }

    /// <summary>
    /// 获取访问密钥显示名称。
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; }
}

/// <summary>
/// 表示请求日志列表中的单条事件。
/// </summary>
public sealed class LogEventResponse
{
    /// <summary>
    /// 初始化请求日志列表中的单条事件。
    /// </summary>
    /// <param name="id">日志标识。</param>
    /// <param name="requestId">请求标识。</param>
    /// <param name="createdAt">创建时间戳。</param>
    /// <param name="method">请求方法。</param>
    /// <param name="path">请求路径。</param>
    /// <param name="clientIp">客户端地址。</param>
    /// <param name="model">请求模型。</param>
    /// <param name="upstreamModel">上游模型。</param>
    /// <param name="channelId">通道标识。</param>
    /// <param name="isStream">是否为流式请求的数值标记。</param>
    /// <param name="ttftMs">首字耗时毫秒数。</param>
    /// <param name="durationMs">总耗时毫秒数。</param>
    /// <param name="statusCode">响应状态码。</param>
    /// <param name="inputTokens">输入令牌数。</param>
    /// <param name="cachedTokens">缓存令牌数。</param>
    /// <param name="outputTokens">输出令牌数。</param>
    /// <param name="cost">请求成本。</param>
    /// <param name="ownerUsername">所属用户名。</param>
    /// <param name="apiKeyId">访问密钥标识。</param>
    /// <param name="apiKeyName">访问密钥显示名称。</param>
    /// <param name="error">错误消息。</param>
    /// <param name="requestStatus">请求状态。</param>
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
        string? apiKeyName,
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
        ApiKeyName = apiKeyName;
        Error = error;
        RequestStatus = requestStatus;
    }

    /// <summary>
    /// 获取日志标识。
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; }

    /// <summary>
    /// 获取请求标识。
    /// </summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; }

    /// <summary>
    /// 获取创建时间戳。
    /// </summary>
    [JsonPropertyName("created_at")]
    public double? CreatedAt { get; }

    /// <summary>
    /// 获取请求方法。
    /// </summary>
    [JsonPropertyName("method")]
    public string? Method { get; }

    /// <summary>
    /// 获取请求路径。
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; }

    /// <summary>
    /// 获取客户端地址。
    /// </summary>
    [JsonPropertyName("client_ip")]
    public string? ClientIp { get; }

    /// <summary>
    /// 获取请求模型。
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; }

    /// <summary>
    /// 获取上游模型。
    /// </summary>
    [JsonPropertyName("upstream_model")]
    public string? UpstreamModel { get; }

    /// <summary>
    /// 获取通道标识。
    /// </summary>
    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; }

    /// <summary>
    /// 获取是否为流式请求的数值标记。
    /// </summary>
    [JsonPropertyName("is_stream")]
    public int IsStream { get; }

    /// <summary>
    /// 获取首字耗时毫秒数。
    /// </summary>
    [JsonPropertyName("ttft_ms")]
    public int? TtftMs { get; }

    /// <summary>
    /// 获取总耗时毫秒数。
    /// </summary>
    [JsonPropertyName("duration_ms")]
    public int? DurationMs { get; }

    /// <summary>
    /// 获取响应状态码。
    /// </summary>
    [JsonPropertyName("status_code")]
    public int? StatusCode { get; }

    /// <summary>
    /// 获取输入令牌数。
    /// </summary>
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; }

    /// <summary>
    /// 获取缓存令牌数。
    /// </summary>
    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; }

    /// <summary>
    /// 获取输出令牌数。
    /// </summary>
    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; }

    /// <summary>
    /// 获取请求成本。
    /// </summary>
    [JsonPropertyName("cost")]
    public double Cost { get; }

    /// <summary>
    /// 获取所属用户名。
    /// </summary>
    [JsonPropertyName("owner_username")]
    public string? OwnerUsername { get; }

    /// <summary>
    /// 获取访问密钥标识。
    /// </summary>
    [JsonPropertyName("api_key_id")]
    public long? ApiKeyId { get; }

    /// <summary>
    /// 获取访问密钥显示名称。
    /// </summary>
    [JsonPropertyName("api_key_name")]
    public string? ApiKeyName { get; }

    /// <summary>
    /// 获取错误消息。
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; }

    /// <summary>
    /// 获取请求状态。
    /// </summary>
    [JsonPropertyName("request_status")]
    public string RequestStatus { get; }

    /// <summary>
    /// 根据日志事件数据创建响应对象。
    /// </summary>
    /// <param name="log">日志事件数据。</param>
    /// <returns>请求日志列表事件响应。</returns>
    public static LogEventResponse From(
        RequestLogEventDto log,
        IReadOnlyDictionary<long, string>? apiKeyNames = null)
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
            ReadApiKeyName(log.ApiKeyId, apiKeyNames),
            log.Error,
            log.RequestStatus);
    }

    private static string? ReadApiKeyName(
        long? apiKeyId,
        IReadOnlyDictionary<long, string>? apiKeyNames)
    {
        return apiKeyId.HasValue && apiKeyNames?.TryGetValue(apiKeyId.Value, out var name) == true
            ? name
            : null;
    }
}

/// <summary>
/// 表示请求日志详情响应。
/// </summary>
public sealed class LogDetailResponse
{
    /// <summary>
    /// 初始化请求日志详情响应。
    /// </summary>
    /// <param name="id">日志标识。</param>
    /// <param name="requestId">请求标识。</param>
    /// <param name="createdAt">创建时间戳。</param>
    /// <param name="method">请求方法。</param>
    /// <param name="path">请求路径。</param>
    /// <param name="clientIp">客户端地址。</param>
    /// <param name="model">请求模型。</param>
    /// <param name="upstreamModel">上游模型。</param>
    /// <param name="channelId">通道标识。</param>
    /// <param name="isStream">是否为流式请求的数值标记。</param>
    /// <param name="ttftMs">首字耗时毫秒数。</param>
    /// <param name="durationMs">总耗时毫秒数。</param>
    /// <param name="statusCode">响应状态码。</param>
    /// <param name="inputTokens">输入令牌数。</param>
    /// <param name="cachedTokens">缓存令牌数。</param>
    /// <param name="outputTokens">输出令牌数。</param>
    /// <param name="cost">请求成本。</param>
    /// <param name="ownerUsername">所属用户名。</param>
    /// <param name="apiKeyId">访问密钥标识。</param>
    /// <param name="apiKeyName">访问密钥显示名称。</param>
    /// <param name="error">错误消息。</param>
    /// <param name="requestStatus">请求状态。</param>
    /// <param name="requestHeaders">请求头内容。</param>
    /// <param name="requestBody">请求体内容。</param>
    /// <param name="upstreamRequestBody">上游请求体内容。</param>
    /// <param name="upstreamResponseBody">上游响应体内容。</param>
    /// <param name="responseBody">响应体内容。</param>
    /// <param name="webSearchJson">联网搜索记录内容。</param>
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
        string? apiKeyName,
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
        ApiKeyName = apiKeyName;
        Error = error;
        RequestStatus = requestStatus;
        RequestHeaders = requestHeaders;
        RequestBody = requestBody;
        UpstreamRequestBody = upstreamRequestBody;
        UpstreamResponseBody = upstreamResponseBody;
        ResponseBody = responseBody;
        WebSearchJson = webSearchJson;
    }

    /// <summary>
    /// 获取日志标识。
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; }

    /// <summary>
    /// 获取请求标识。
    /// </summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; }

    /// <summary>
    /// 获取创建时间戳。
    /// </summary>
    [JsonPropertyName("created_at")]
    public double? CreatedAt { get; }

    /// <summary>
    /// 获取请求方法。
    /// </summary>
    [JsonPropertyName("method")]
    public string? Method { get; }

    /// <summary>
    /// 获取请求路径。
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; }

    /// <summary>
    /// 获取客户端地址。
    /// </summary>
    [JsonPropertyName("client_ip")]
    public string? ClientIp { get; }

    /// <summary>
    /// 获取请求模型。
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; }

    /// <summary>
    /// 获取上游模型。
    /// </summary>
    [JsonPropertyName("upstream_model")]
    public string? UpstreamModel { get; }

    /// <summary>
    /// 获取通道标识。
    /// </summary>
    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; }

    /// <summary>
    /// 获取是否为流式请求的数值标记。
    /// </summary>
    [JsonPropertyName("is_stream")]
    public int IsStream { get; }

    /// <summary>
    /// 获取首字耗时毫秒数。
    /// </summary>
    [JsonPropertyName("ttft_ms")]
    public int? TtftMs { get; }

    /// <summary>
    /// 获取总耗时毫秒数。
    /// </summary>
    [JsonPropertyName("duration_ms")]
    public int? DurationMs { get; }

    /// <summary>
    /// 获取响应状态码。
    /// </summary>
    [JsonPropertyName("status_code")]
    public int? StatusCode { get; }

    /// <summary>
    /// 获取输入令牌数。
    /// </summary>
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; }

    /// <summary>
    /// 获取缓存令牌数。
    /// </summary>
    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; }

    /// <summary>
    /// 获取输出令牌数。
    /// </summary>
    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; }

    /// <summary>
    /// 获取请求成本。
    /// </summary>
    [JsonPropertyName("cost")]
    public double Cost { get; }

    /// <summary>
    /// 获取所属用户名。
    /// </summary>
    [JsonPropertyName("owner_username")]
    public string? OwnerUsername { get; }

    /// <summary>
    /// 获取访问密钥标识。
    /// </summary>
    [JsonPropertyName("api_key_id")]
    public long? ApiKeyId { get; }

    /// <summary>
    /// 获取访问密钥显示名称。
    /// </summary>
    [JsonPropertyName("api_key_name")]
    public string? ApiKeyName { get; }

    /// <summary>
    /// 获取错误消息。
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; }

    /// <summary>
    /// 获取请求状态。
    /// </summary>
    [JsonPropertyName("request_status")]
    public string RequestStatus { get; }

    /// <summary>
    /// 获取请求头内容。
    /// </summary>
    [JsonPropertyName("request_headers")]
    public string? RequestHeaders { get; }

    /// <summary>
    /// 获取请求体内容。
    /// </summary>
    [JsonPropertyName("request_body")]
    public string? RequestBody { get; }

    /// <summary>
    /// 获取上游请求体内容。
    /// </summary>
    [JsonPropertyName("upstream_request_body")]
    public string? UpstreamRequestBody { get; }

    /// <summary>
    /// 获取上游响应体内容。
    /// </summary>
    [JsonPropertyName("upstream_response_body")]
    public string? UpstreamResponseBody { get; }

    /// <summary>
    /// 获取响应体内容。
    /// </summary>
    [JsonPropertyName("response_body")]
    public string? ResponseBody { get; }

    /// <summary>
    /// 获取联网搜索记录内容。
    /// </summary>
    [JsonPropertyName("web_search_json")]
    public string? WebSearchJson { get; }

    /// <summary>
    /// 根据请求日志详情数据创建响应对象。
    /// </summary>
    /// <param name="log">请求日志详情数据。</param>
    /// <returns>请求日志详情响应。</returns>
    public static LogDetailResponse From(
        RequestLogDto log,
        IReadOnlyDictionary<long, string>? apiKeyNames = null)
    {
        var logEvent = LogEventResponse.From(new RequestLogEventDto(
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
            log.RequestStatus), apiKeyNames);

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
            logEvent.ApiKeyName,
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

/// <summary>
/// 表示后台观测统计响应。
/// </summary>
public sealed class StatsResponse
{
    /// <summary>
    /// 初始化后台观测统计响应。
    /// </summary>
    /// <param name="range">统计范围名称。</param>
    /// <param name="start">统计开始时间。</param>
    /// <param name="end">统计结束时间。</param>
    /// <param name="granularityMinutes">统计粒度分钟数。</param>
    /// <param name="currencyRate">币种换算比例。</param>
    /// <param name="summary">统计汇总。</param>
    /// <param name="points">统计时间点列表。</param>
    /// <param name="modelDistribution">模型分布列表。</param>
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

    /// <summary>
    /// 获取统计范围名称。
    /// </summary>
    [JsonPropertyName("range")]
    public string Range { get; }

    /// <summary>
    /// 获取统计开始时间。
    /// </summary>
    [JsonPropertyName("start")]
    public string Start { get; }

    /// <summary>
    /// 获取统计结束时间。
    /// </summary>
    [JsonPropertyName("end")]
    public string End { get; }

    /// <summary>
    /// 获取统计粒度分钟数。
    /// </summary>
    [JsonPropertyName("granularity_minutes")]
    public int GranularityMinutes { get; }

    /// <summary>
    /// 获取币种换算比例。
    /// </summary>
    [JsonPropertyName("currency_rate")]
    public double CurrencyRate { get; }

    /// <summary>
    /// 获取统计汇总。
    /// </summary>
    [JsonPropertyName("summary")]
    public StatsSummaryResponse Summary { get; }

    /// <summary>
    /// 获取统计时间点列表。
    /// </summary>
    [JsonPropertyName("points")]
    public IReadOnlyList<StatsPointResponse> Points { get; }

    /// <summary>
    /// 获取模型分布列表。
    /// </summary>
    [JsonPropertyName("model_distribution")]
    public IReadOnlyList<StatsModelDistributionResponse> ModelDistribution { get; }

    /// <summary>
    /// 根据统计数据创建响应对象。
    /// </summary>
    /// <param name="stats">统计数据。</param>
    /// <returns>后台观测统计响应。</returns>
    public static StatsResponse From(StatsDto stats)
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

/// <summary>
/// 表示后台观测统计汇总响应。
/// </summary>
public sealed class StatsSummaryResponse
{
    /// <summary>
    /// 初始化后台观测统计汇总响应。
    /// </summary>
    /// <param name="requestCount">请求总数。</param>
    /// <param name="successCount">成功请求数。</param>
    /// <param name="recent1hRequestCount">最近一小时请求数。</param>
    /// <param name="inputTokens">输入令牌数。</param>
    /// <param name="cachedTokens">缓存令牌数。</param>
    /// <param name="outputTokens">输出令牌数。</param>
    /// <param name="totalTokens">令牌总数。</param>
    /// <param name="recent1hTokens">最近一小时令牌数。</param>
    /// <param name="cost">总成本。</param>
    /// <param name="recent1hCost">最近一小时成本。</param>
    /// <param name="rpm">每分钟请求数。</param>
    /// <param name="tpm">每分钟令牌数。</param>
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

    /// <summary>
    /// 获取请求总数。
    /// </summary>
    [JsonPropertyName("request_count")]
    public int RequestCount { get; }

    /// <summary>
    /// 获取成功请求数。
    /// </summary>
    [JsonPropertyName("success_count")]
    public int SuccessCount { get; }

    /// <summary>
    /// 获取最近一小时请求数。
    /// </summary>
    [JsonPropertyName("recent_1h_request_count")]
    public int Recent1hRequestCount { get; }

    /// <summary>
    /// 获取输入令牌数。
    /// </summary>
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; }

    /// <summary>
    /// 获取缓存令牌数。
    /// </summary>
    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; }

    /// <summary>
    /// 获取输出令牌数。
    /// </summary>
    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; }

    /// <summary>
    /// 获取令牌总数。
    /// </summary>
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; }

    /// <summary>
    /// 获取最近一小时令牌数。
    /// </summary>
    [JsonPropertyName("recent_1h_tokens")]
    public int Recent1hTokens { get; }

    /// <summary>
    /// 获取总成本。
    /// </summary>
    [JsonPropertyName("cost")]
    public double Cost { get; }

    /// <summary>
    /// 获取最近一小时成本。
    /// </summary>
    [JsonPropertyName("recent_1h_cost")]
    public double Recent1hCost { get; }

    /// <summary>
    /// 获取每分钟请求数。
    /// </summary>
    [JsonPropertyName("rpm")]
    public double Rpm { get; }

    /// <summary>
    /// 获取每分钟令牌数。
    /// </summary>
    [JsonPropertyName("tpm")]
    public double Tpm { get; }

    /// <summary>
    /// 根据统计汇总数据创建响应对象。
    /// </summary>
    /// <param name="summary">统计汇总数据。</param>
    /// <returns>后台观测统计汇总响应。</returns>
    public static StatsSummaryResponse From(StatsSummaryDto summary)
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

/// <summary>
/// 表示后台观测统计时间点响应。
/// </summary>
public sealed class StatsPointResponse
{
    /// <summary>
    /// 初始化后台观测统计时间点响应。
    /// </summary>
    /// <param name="time">统计时间。</param>
    /// <param name="cost">成本。</param>
    /// <param name="inputTokens">输入令牌数。</param>
    /// <param name="cachedTokens">缓存令牌数。</param>
    /// <param name="outputTokens">输出令牌数。</param>
    /// <param name="avgTtftMs">平均首字耗时毫秒数。</param>
    /// <param name="cacheHitRate">缓存命中率。</param>
    /// <param name="rpm">每分钟请求数。</param>
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

    /// <summary>
    /// 获取统计时间。
    /// </summary>
    [JsonPropertyName("time")]
    public string Time { get; }

    /// <summary>
    /// 获取成本。
    /// </summary>
    [JsonPropertyName("cost")]
    public double Cost { get; }

    /// <summary>
    /// 获取输入令牌数。
    /// </summary>
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; }

    /// <summary>
    /// 获取缓存令牌数。
    /// </summary>
    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; }

    /// <summary>
    /// 获取输出令牌数。
    /// </summary>
    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; }

    /// <summary>
    /// 获取平均首字耗时毫秒数。
    /// </summary>
    [JsonPropertyName("avg_ttft_ms")]
    public double? AvgTtftMs { get; }

    /// <summary>
    /// 获取缓存命中率。
    /// </summary>
    [JsonPropertyName("cache_hit_rate")]
    public double? CacheHitRate { get; }

    /// <summary>
    /// 获取每分钟请求数。
    /// </summary>
    [JsonPropertyName("rpm")]
    public double Rpm { get; }

    /// <summary>
    /// 根据统计时间点数据创建响应对象。
    /// </summary>
    /// <param name="point">统计时间点数据。</param>
    /// <returns>后台观测统计时间点响应。</returns>
    public static StatsPointResponse From(StatsPointDto point)
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

/// <summary>
/// 表示模型分布统计响应。
/// </summary>
public sealed class StatsModelDistributionResponse
{
    /// <summary>
    /// 初始化模型分布统计响应。
    /// </summary>
    /// <param name="model">模型名称。</param>
    /// <param name="count">请求数量。</param>
    public StatsModelDistributionResponse(string model, int count)
    {
        Model = model;
        Count = count;
    }

    /// <summary>
    /// 获取模型名称。
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; }

    /// <summary>
    /// 获取请求数量。
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; }

    /// <summary>
    /// 根据模型分布数据创建响应对象。
    /// </summary>
    /// <param name="item">模型分布数据。</param>
    /// <returns>模型分布统计响应。</returns>
    public static StatsModelDistributionResponse From(ModelDistributionDto item)
    {
        return new StatsModelDistributionResponse(item.Model, item.Count);
    }
}

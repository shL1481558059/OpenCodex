using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.DTOs;

/// <summary>
/// 表示代理请求日志写入操作要持久化的值。
/// </summary>
/// <param name="requestId">唯一请求标识符。</param>
/// <param name="createdAt">请求创建时间戳。</param>
/// <param name="method">传入 HTTP 方法。</param>
/// <param name="path">传入请求路径。</param>
/// <param name="clientIp">客户端 IP 地址（如果可用）。</param>
/// <param name="requestHeaders">序列化后的传入请求头。</param>
/// <param name="requestBody">序列化后的传入请求体。</param>
/// <param name="upstreamRequestBody">序列化后的上游请求体。</param>
/// <param name="upstreamResponseBody">序列化后的上游响应体。</param>
/// <param name="responseBody">序列化后的下游响应体。</param>
/// <param name="webSearchJson">序列化后的 Web 搜索详情（如果可用）。</param>
/// <param name="model">请求的模型（如果可用）。</param>
/// <param name="upstreamModel">上游模型（如果可用）。</param>
/// <param name="channelId">选中的通道标识符（如果可用）。</param>
/// <param name="requestType">请求日志类型。</param>
/// <param name="parentRequestLogId">父请求日志标识符（如果可用）。</param>
/// <param name="isStream">指示请求是否使用流式响应的值。</param>
/// <param name="ttftMs">首 token 时间，单位为毫秒（如果可用）。</param>
/// <param name="durationMs">总请求耗时，单位为毫秒。</param>
/// <param name="statusCode">响应状态码。</param>
/// <param name="inputTokens">输入 token 数。</param>
/// <param name="cachedTokens">缓存输入 token 数。</param>
/// <param name="outputTokens">输出 token 数。</param>
/// <param name="cost">计算得到的请求成本。</param>
/// <param name="ownerUsername">拥有该请求的用户名。</param>
/// <param name="apiKeyId">请求使用的 API 密钥标识符（如果可用）。</param>
/// <param name="error">错误消息（如果可用）。</param>
public sealed class RequestLogWriteDto(
    string requestId,
    double createdAt,
    double? processingStartedAt,
    double? completedAt,
    string? lifecycleStatus,
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
    string requestType,
    long? parentRequestLogId,
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
    string? error,
    string? ocrJson,
    string? streamTimingsJson,
    IReadOnlyList<ProxyRequestStreamLineCapture>? streamLines)
{
    /// <summary>
    /// 获取唯一请求标识符。
    /// </summary>
    public string RequestId { get; } = requestId;

    /// <summary>
    /// 获取请求创建时间戳。
    /// </summary>
    public double CreatedAt { get; } = createdAt;

    /// <summary>
    /// 获取请求进入处理状态的时间戳（如果可用）。
    /// </summary>
    public double? ProcessingStartedAt { get; } = processingStartedAt;

    /// <summary>
    /// 获取请求完成时间戳（如果可用）。
    /// </summary>
    public double? CompletedAt { get; } = completedAt;

    /// <summary>
    /// 获取请求生命周期状态。
    /// </summary>
    public string? LifecycleStatus { get; } = lifecycleStatus;

    /// <summary>
    /// 获取传入 HTTP 方法。
    /// </summary>
    public string Method { get; } = method;

    /// <summary>
    /// 获取传入请求路径。
    /// </summary>
    public string Path { get; } = path;

    /// <summary>
    /// 获取客户端 IP 地址（如果可用）。
    /// </summary>
    public string? ClientIp { get; } = clientIp;

    /// <summary>
    /// 获取序列化后的传入请求头。
    /// </summary>
    public string RequestHeaders { get; } = requestHeaders;

    /// <summary>
    /// 获取序列化后的传入请求体。
    /// </summary>
    public string RequestBody { get; } = requestBody;

    /// <summary>
    /// 获取序列化后的上游请求体。
    /// </summary>
    public string UpstreamRequestBody { get; } = upstreamRequestBody;

    /// <summary>
    /// 获取序列化后的上游响应体。
    /// </summary>
    public string UpstreamResponseBody { get; } = upstreamResponseBody;

    /// <summary>
    /// 获取序列化后的下游响应体。
    /// </summary>
    public string ResponseBody { get; } = responseBody;

    /// <summary>
    /// 获取序列化后的 Web 搜索详情（如果可用）。
    /// </summary>
    public string? WebSearchJson { get; } = webSearchJson;

    /// <summary>
    /// 获取请求的模型（如果可用）。
    /// </summary>
    public string? Model { get; } = model;

    /// <summary>
    /// 获取上游模型（如果可用）。
    /// </summary>
    public string? UpstreamModel { get; } = upstreamModel;

    /// <summary>
    /// 获取选中的通道标识符（如果可用）。
    /// </summary>
    public string? ChannelId { get; } = channelId;

    /// <summary>
    /// 获取请求日志类型。
    /// </summary>
    public string RequestType { get; } = requestType;

    /// <summary>
    /// 获取父请求日志标识符（如果可用）。
    /// </summary>
    public long? ParentRequestLogId { get; } = parentRequestLogId;

    /// <summary>
    /// 获取指示请求是否使用流式响应的值。
    /// </summary>
    public bool IsStream { get; } = isStream;

    /// <summary>
    /// 获取首 token 时间，单位为毫秒（如果可用）。
    /// </summary>
    public int? TtftMs { get; } = ttftMs;

    /// <summary>
    /// 获取总请求耗时，单位为毫秒。
    /// </summary>
    public int DurationMs { get; } = durationMs;

    /// <summary>
    /// 获取响应状态码。
    /// </summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>
    /// 获取输入 token 数。
    /// </summary>
    public int InputTokens { get; } = inputTokens;

    /// <summary>
    /// 获取缓存输入 token 数。
    /// </summary>
    public int CachedTokens { get; } = cachedTokens;

    /// <summary>
    /// 获取输出 token 数。
    /// </summary>
    public int OutputTokens { get; } = outputTokens;

    /// <summary>
    /// 获取计算得到的请求成本。
    /// </summary>
    public double Cost { get; } = cost;

    /// <summary>
    /// 获取拥有该请求的用户名。
    /// </summary>
    public string OwnerUsername { get; } = ownerUsername;

    /// <summary>
    /// 获取请求使用的 API 密钥标识符（如果可用）。
    /// </summary>
    public long? ApiKeyId { get; } = apiKeyId;

    /// <summary>
    /// 获取错误消息（如果可用）。
    /// </summary>
    public string? Error { get; } = error;

    /// <summary>
    /// 获取序列化后的 OCR 详情（如果可用）。
    /// </summary>
    public string? OcrJson { get; } = ocrJson;

    /// <summary>
    /// 获取序列化后的流式写出时序诊断（如果可用）。
    /// </summary>
    public string? StreamTimingsJson { get; } = streamTimingsJson;

    /// <summary>
    /// 获取按原始 SSE line 记录的上游流片段。
    /// </summary>
    public IReadOnlyList<ProxyRequestStreamLineCapture>? StreamLines { get; } = streamLines;
}

/// <summary>
/// 表示请求日志中的一条原始流式响应行。
/// </summary>
/// <param name="sequence">该行在请求内的顺序。</param>
/// <param name="occurredAt">记录该行的时间戳。</param>
/// <param name="source">该行来源。</param>
/// <param name="rawLine">原始行文本。</param>
public sealed class RequestLogStreamLineDto(
    int sequence,
    double occurredAt,
    string source,
    string rawLine)
{
    /// <summary>
    /// 获取该行在请求内的顺序。
    /// </summary>
    public int Sequence { get; } = sequence;

    /// <summary>
    /// 获取记录该行的时间戳。
    /// </summary>
    public double OccurredAt { get; } = occurredAt;

    /// <summary>
    /// 获取该行来源。
    /// </summary>
    public string Source { get; } = source;

    /// <summary>
    /// 获取原始行文本。
    /// </summary>
    public string RawLine { get; } = rawLine;
}

/// <summary>
/// 表示日志查询接口返回的详细请求日志条目。
/// </summary>
/// <param name="id">请求日志的数据库标识符。</param>
/// <param name="requestId">唯一请求标识符（如果可用）。</param>
/// <param name="createdAt">请求创建时间戳（如果可用）。</param>
/// <param name="method">传入 HTTP 方法（如果可用）。</param>
/// <param name="path">传入请求路径（如果可用）。</param>
/// <param name="clientIp">客户端 IP 地址（如果可用）。</param>
/// <param name="model">请求的模型（如果可用）。</param>
/// <param name="upstreamModel">上游模型（如果可用）。</param>
/// <param name="channelId">选中的通道标识符（如果可用）。</param>
/// <param name="requestType">请求日志类型。</param>
/// <param name="parentRequestLogId">父请求日志标识符（如果可用）。</param>
/// <param name="isStream">指示请求是否使用流式响应的值。</param>
/// <param name="ttftMs">首 token 时间，单位为毫秒（如果可用）。</param>
/// <param name="durationMs">总请求耗时，单位为毫秒（如果可用）。</param>
/// <param name="statusCode">响应状态码（如果可用）。</param>
/// <param name="inputTokens">输入 token 数。</param>
/// <param name="cachedTokens">缓存输入 token 数。</param>
/// <param name="outputTokens">输出 token 数。</param>
/// <param name="cost">计算得到的请求成本。</param>
/// <param name="ownerUsername">拥有该请求的用户名。</param>
/// <param name="apiKeyId">请求使用的 API 密钥标识符（如果可用）。</param>
/// <param name="error">错误消息（如果可用）。</param>
/// <param name="requestHeaders">序列化后的传入请求头（如果可用）。</param>
/// <param name="requestBody">序列化后的传入请求体（如果可用）。</param>
/// <param name="upstreamRequestBody">序列化后的上游请求体（如果可用）。</param>
/// <param name="upstreamResponseBody">序列化后的上游响应体（如果可用）。</param>
/// <param name="responseBody">序列化后的下游响应体（如果可用）。</param>
/// <param name="webSearchJson">序列化后的 Web 搜索详情（如果可用）。</param>
/// <param name="ocrJson">序列化后的 OCR 详情（如果可用）。</param>
/// <param name="streamTimingsJson">序列化后的流式写出时序诊断（如果可用）。</param>
/// <param name="requestStatus">标准化后的请求状态。</param>
public sealed class RequestLogDto(
    long id,
    string? requestId,
    double? createdAt,
    double? processingStartedAt,
    double? completedAt,
    string? method,
    string? path,
    string? clientIp,
    string? model,
    string? upstreamModel,
    string? channelId,
    string requestType,
    long? parentRequestLogId,
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
    string? ocrJson,
    string? streamTimingsJson,
    IReadOnlyList<RequestLogStreamLineDto> streamLines,
    string requestStatus)
{
    /// <summary>
    /// 获取请求日志的数据库标识符。
    /// </summary>
    public long Id { get; } = id;

    /// <summary>
    /// 获取唯一请求标识符（如果可用）。
    /// </summary>
    public string? RequestId { get; } = requestId;

    /// <summary>
    /// 获取请求创建时间戳（如果可用）。
    /// </summary>
    public double? CreatedAt { get; } = createdAt;

    /// <summary>
    /// 获取请求进入处理状态的时间戳（如果可用）。
    /// </summary>
    public double? ProcessingStartedAt { get; } = processingStartedAt;

    /// <summary>
    /// 获取请求完成时间戳（如果可用）。
    /// </summary>
    public double? CompletedAt { get; } = completedAt;

    /// <summary>
    /// 获取传入 HTTP 方法（如果可用）。
    /// </summary>
    public string? Method { get; } = method;

    /// <summary>
    /// 获取传入请求路径（如果可用）。
    /// </summary>
    public string? Path { get; } = path;

    /// <summary>
    /// 获取客户端 IP 地址（如果可用）。
    /// </summary>
    public string? ClientIp { get; } = clientIp;

    /// <summary>
    /// 获取请求的模型（如果可用）。
    /// </summary>
    public string? Model { get; } = model;

    /// <summary>
    /// 获取上游模型（如果可用）。
    /// </summary>
    public string? UpstreamModel { get; } = upstreamModel;

    /// <summary>
    /// 获取选中的通道标识符（如果可用）。
    /// </summary>
    public string? ChannelId { get; } = channelId;

    /// <summary>
    /// 获取请求日志类型。
    /// </summary>
    public string RequestType { get; } = requestType;

    /// <summary>
    /// 获取父请求日志标识符（如果可用）。
    /// </summary>
    public long? ParentRequestLogId { get; } = parentRequestLogId;

    /// <summary>
    /// 获取指示请求是否使用流式响应的值。
    /// </summary>
    public bool IsStream { get; } = isStream;

    /// <summary>
    /// 获取首 token 时间，单位为毫秒（如果可用）。
    /// </summary>
    public int? TtftMs { get; } = ttftMs;

    /// <summary>
    /// 获取总请求耗时，单位为毫秒（如果可用）。
    /// </summary>
    public int? DurationMs { get; } = durationMs;

    /// <summary>
    /// 获取响应状态码（如果可用）。
    /// </summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>
    /// 获取输入 token 数。
    /// </summary>
    public int InputTokens { get; } = inputTokens;

    /// <summary>
    /// 获取缓存输入 token 数。
    /// </summary>
    public int CachedTokens { get; } = cachedTokens;

    /// <summary>
    /// 获取输出 token 数。
    /// </summary>
    public int OutputTokens { get; } = outputTokens;

    /// <summary>
    /// 获取计算得到的请求成本。
    /// </summary>
    public double Cost { get; } = cost;

    /// <summary>
    /// 获取拥有该请求的用户名。
    /// </summary>
    public string OwnerUsername { get; } = ownerUsername;

    /// <summary>
    /// 获取请求使用的 API 密钥标识符（如果可用）。
    /// </summary>
    public long? ApiKeyId { get; } = apiKeyId;

    /// <summary>
    /// 获取错误消息（如果可用）。
    /// </summary>
    public string? Error { get; } = error;

    /// <summary>
    /// 获取序列化后的传入请求头（如果可用）。
    /// </summary>
    public string? RequestHeaders { get; } = requestHeaders;

    /// <summary>
    /// 获取序列化后的传入请求体（如果可用）。
    /// </summary>
    public string? RequestBody { get; } = requestBody;

    /// <summary>
    /// 获取序列化后的上游请求体（如果可用）。
    /// </summary>
    public string? UpstreamRequestBody { get; } = upstreamRequestBody;

    /// <summary>
    /// 获取序列化后的上游响应体（如果可用）。
    /// </summary>
    public string? UpstreamResponseBody { get; } = upstreamResponseBody;

    /// <summary>
    /// 获取序列化后的下游响应体（如果可用）。
    /// </summary>
    public string? ResponseBody { get; } = responseBody;

    /// <summary>
    /// 获取序列化后的 Web 搜索详情（如果可用）。
    /// </summary>
    public string? WebSearchJson { get; } = webSearchJson;

    /// <summary>
    /// 获取序列化后的 OCR 详情（如果可用）。
    /// </summary>
    public string? OcrJson { get; } = ocrJson;

    /// <summary>
    /// 获取序列化后的流式写出时序诊断（如果可用）。
    /// </summary>
    public string? StreamTimingsJson { get; } = streamTimingsJson;

    /// <summary>
    /// 获取按原始 SSE line 记录的上游流片段。
    /// </summary>
    public IReadOnlyList<RequestLogStreamLineDto> StreamLines { get; } = streamLines;

    /// <summary>
    /// 获取标准化后的请求状态。
    /// </summary>
    public string RequestStatus { get; } = requestStatus;
}

/// <summary>
/// 表示日志列表中返回的请求日志摘要项。
/// </summary>
/// <param name="id">请求日志的数据库标识符。</param>
/// <param name="requestId">唯一请求标识符（如果可用）。</param>
/// <param name="createdAt">请求创建时间戳（如果可用）。</param>
/// <param name="method">传入 HTTP 方法（如果可用）。</param>
/// <param name="path">传入请求路径（如果可用）。</param>
/// <param name="clientIp">客户端 IP 地址（如果可用）。</param>
/// <param name="model">请求的模型（如果可用）。</param>
/// <param name="upstreamModel">上游模型（如果可用）。</param>
/// <param name="channelId">选中的通道标识符（如果可用）。</param>
/// <param name="requestType">请求日志类型。</param>
/// <param name="parentRequestLogId">父请求日志标识符（如果可用）。</param>
/// <param name="isStream">指示请求是否使用流式响应的值。</param>
/// <param name="ttftMs">首 token 时间，单位为毫秒（如果可用）。</param>
/// <param name="durationMs">总请求耗时，单位为毫秒（如果可用）。</param>
/// <param name="statusCode">响应状态码（如果可用）。</param>
/// <param name="inputTokens">输入 token 数。</param>
/// <param name="cachedTokens">缓存输入 token 数。</param>
/// <param name="outputTokens">输出 token 数。</param>
/// <param name="cost">计算得到的请求成本。</param>
/// <param name="ownerUsername">拥有该请求的用户名。</param>
/// <param name="apiKeyId">请求使用的 API 密钥标识符（如果可用）。</param>
/// <param name="error">错误消息（如果可用）。</param>
/// <param name="requestStatus">标准化后的请求状态。</param>
public sealed class RequestLogEventDto(
    long id,
    string? requestId,
    double? createdAt,
    double? processingStartedAt,
    double? completedAt,
    string? method,
    string? path,
    string? clientIp,
    string? model,
    string? upstreamModel,
    string? channelId,
    string requestType,
    long? parentRequestLogId,
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
    /// <summary>
    /// 获取请求日志的数据库标识符。
    /// </summary>
    public long Id { get; } = id;

    /// <summary>
    /// 获取唯一请求标识符（如果可用）。
    /// </summary>
    public string? RequestId { get; } = requestId;

    /// <summary>
    /// 获取请求创建时间戳（如果可用）。
    /// </summary>
    public double? CreatedAt { get; } = createdAt;

    /// <summary>
    /// 获取请求进入处理状态的时间戳（如果可用）。
    /// </summary>
    public double? ProcessingStartedAt { get; } = processingStartedAt;

    /// <summary>
    /// 获取请求完成时间戳（如果可用）。
    /// </summary>
    public double? CompletedAt { get; } = completedAt;

    /// <summary>
    /// 获取传入 HTTP 方法（如果可用）。
    /// </summary>
    public string? Method { get; } = method;

    /// <summary>
    /// 获取传入请求路径（如果可用）。
    /// </summary>
    public string? Path { get; } = path;

    /// <summary>
    /// 获取客户端 IP 地址（如果可用）。
    /// </summary>
    public string? ClientIp { get; } = clientIp;

    /// <summary>
    /// 获取请求的模型（如果可用）。
    /// </summary>
    public string? Model { get; } = model;

    /// <summary>
    /// 获取上游模型（如果可用）。
    /// </summary>
    public string? UpstreamModel { get; } = upstreamModel;

    /// <summary>
    /// 获取选中的通道标识符（如果可用）。
    /// </summary>
    public string? ChannelId { get; } = channelId;

    /// <summary>
    /// 获取请求日志类型。
    /// </summary>
    public string RequestType { get; } = requestType;

    /// <summary>
    /// 获取父请求日志标识符（如果可用）。
    /// </summary>
    public long? ParentRequestLogId { get; } = parentRequestLogId;

    /// <summary>
    /// 获取指示请求是否使用流式响应的值。
    /// </summary>
    public bool IsStream { get; } = isStream;

    /// <summary>
    /// 获取首 token 时间，单位为毫秒（如果可用）。
    /// </summary>
    public int? TtftMs { get; } = ttftMs;

    /// <summary>
    /// 获取总请求耗时，单位为毫秒（如果可用）。
    /// </summary>
    public int? DurationMs { get; } = durationMs;

    /// <summary>
    /// 获取响应状态码（如果可用）。
    /// </summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>
    /// 获取输入 token 数。
    /// </summary>
    public int InputTokens { get; } = inputTokens;

    /// <summary>
    /// 获取缓存输入 token 数。
    /// </summary>
    public int CachedTokens { get; } = cachedTokens;

    /// <summary>
    /// 获取输出 token 数。
    /// </summary>
    public int OutputTokens { get; } = outputTokens;

    /// <summary>
    /// 获取计算得到的请求成本。
    /// </summary>
    public double Cost { get; } = cost;

    /// <summary>
    /// 获取拥有该请求的用户名。
    /// </summary>
    public string OwnerUsername { get; } = ownerUsername;

    /// <summary>
    /// 获取请求使用的 API 密钥标识符（如果可用）。
    /// </summary>
    public long? ApiKeyId { get; } = apiKeyId;

    /// <summary>
    /// 获取错误消息（如果可用）。
    /// </summary>
    public string? Error { get; } = error;

    /// <summary>
    /// 获取标准化后的请求状态。
    /// </summary>
    public string RequestStatus { get; } = requestStatus;
}

/// <summary>
/// 表示请求日志摘要项的分页响应。
/// </summary>
/// <param name="events">当前页中的请求日志项。</param>
/// <param name="total">匹配的请求日志总数。</param>
/// <param name="page">当前页码。</param>
/// <param name="pageSize">每页请求的项目数。</param>
public sealed class RequestLogPageDto(
    IReadOnlyList<RequestLogEventDto> events,
    int total,
    int page,
    int pageSize)
{
    /// <summary>
    /// 获取当前页中的请求日志项。
    /// </summary>
    public IReadOnlyList<RequestLogEventDto> Events { get; } = events;

    /// <summary>
    /// 获取匹配的请求日志总数。
    /// </summary>
    public int Total { get; } = total;

    /// <summary>
    /// 获取当前页码。
    /// </summary>
    public int Page { get; } = page;

    /// <summary>
    /// 获取每页请求的项目数。
    /// </summary>
    public int PageSize { get; } = pageSize;
}

/// <summary>
/// 表示请求的 token 使用量。
/// </summary>
/// <param name="inputTokens">输入 token 数。</param>
/// <param name="cachedTokens">缓存输入 token 数。</param>
/// <param name="outputTokens">输出 token 数。</param>
public sealed class UsageDto(
    int inputTokens,
    int cachedTokens,
    int outputTokens)
{
    /// <summary>
    /// 获取输入 token 数。
    /// </summary>
    public int InputTokens { get; } = inputTokens;

    /// <summary>
    /// 获取缓存输入 token 数。
    /// </summary>
    public int CachedTokens { get; } = cachedTokens;

    /// <summary>
    /// 获取输出 token 数。
    /// </summary>
    public int OutputTokens { get; } = outputTokens;
}

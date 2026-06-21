using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// 包含持久化代理请求日志条目所需的完整值集合。
/// </summary>
public sealed class ProxyRequestLogContext
{
    /// <summary>
    /// 初始化 <see cref="ProxyRequestLogContext"/> 类的新实例。
    /// </summary>
    /// <param name="requestId">唯一请求标识符。</param>
    /// <param name="ownerUsername">拥有该请求的用户名。</param>
    /// <param name="apiKeyId">请求使用的访问密钥标识符（如果可用）。</param>
    /// <param name="payload">标准化后的传入请求载荷。</param>
    /// <param name="upstreamRequest">发送到上游的请求载荷。</param>
    /// <param name="upstreamResponse">捕获到的上游响应载荷。</param>
    /// <param name="responsePayload">捕获到的返回给调用方的响应载荷。</param>
    /// <param name="errorResponse">返回给调用方的错误响应（如果可用）。</param>
    /// <param name="requestModel">调用方请求的模型（如果可用）。</param>
    /// <param name="upstreamModel">路由到上游提供方的模型（如果可用）。</param>
    /// <param name="channelId">选中的通道标识符（如果可用）。</param>
    /// <param name="channelType">选中的通道类型（如果可用）。</param>
    /// <param name="isStream">指示请求是否使用流式响应的值。</param>
    /// <param name="ttftMs">首令牌时间，单位为毫秒（如果可用）。</param>
    /// <param name="statusCode">响应状态码。</param>
    /// <param name="durationMs">总请求耗时，单位为毫秒。</param>
    /// <param name="error">错误消息（如果可用）。</param>
    /// <param name="webSearchDetails">联网搜索诊断详情（如果可用）。</param>
    /// <param name="method">传入的超文本传输协议方法。</param>
    /// <param name="path">传入请求路径。</param>
    /// <param name="clientIp">客户端网络地址（如果可用）。</param>
    /// <param name="requestHeaders">标准化后的传入请求头。</param>
    /// <param name="requestType">请求日志类型。</param>
    /// <param name="parentRequestLogId">父请求日志标识符（如果可用）。</param>
    /// <param name="ocrDetails">OCR 专用详情（如果可用）。</param>
    /// <param name="streamWriteMetrics">流式写出时序诊断（如果可用）。</param>
    /// <param name="streamLines">按原始 SSE line 记录的上游流片段（如果可用）。</param>
    public ProxyRequestLogContext(
        string requestId,
        string ownerUsername,
        Guid? apiKeyId,
        Dictionary<string, object?>? payload,
        Dictionary<string, object?>? upstreamRequest,
        Dictionary<string, object?>? upstreamResponse,
        Dictionary<string, object?>? responsePayload,
        object? errorResponse,
        string? requestModel,
        string? upstreamModel,
        string? channelId,
        string? channelType,
        bool isStream,
        int? ttftMs,
        int statusCode,
        int durationMs,
        string? error,
        Dictionary<string, object?>? webSearchDetails,
        string method,
        string path,
        string? clientIp,
        IReadOnlyDictionary<string, string> requestHeaders,
        string requestType = ProxyRequestTypes.Main,
        Guid? parentRequestLogId = null,
        Dictionary<string, object?>? ocrDetails = null,
        StreamWriteMetrics? streamWriteMetrics = null,
        IReadOnlyList<ProxyRequestStreamLineCapture>? streamLines = null)
    {
        RequestId = requestId;
        OwnerUsername = ownerUsername;
        ApiKeyId = apiKeyId;
        Payload = payload;
        UpstreamRequest = upstreamRequest;
        UpstreamResponse = upstreamResponse;
        ResponsePayload = responsePayload;
        ErrorResponse = errorResponse;
        RequestModel = requestModel;
        UpstreamModel = upstreamModel;
        ChannelId = channelId;
        ChannelType = channelType;
        IsStream = isStream;
        TtftMs = ttftMs;
        StatusCode = statusCode;
        DurationMs = durationMs;
        Error = error;
        WebSearchDetails = webSearchDetails;
        Method = method;
        Path = path;
        ClientIp = clientIp;
        RequestHeaders = requestHeaders;
        RequestType = requestType;
        ParentRequestLogId = parentRequestLogId;
        OcrDetails = ocrDetails;
        StreamWriteMetrics = streamWriteMetrics;
        StreamLines = streamLines;
    }

    /// <summary>
    /// 获取唯一请求标识符。
    /// </summary>
    public string RequestId { get; }

    /// <summary>
    /// 获取拥有该请求的用户名。
    /// </summary>
    public string OwnerUsername { get; }

    /// <summary>
    /// 获取请求使用的访问密钥标识符（如果可用）。
    /// </summary>
    public Guid? ApiKeyId { get; }

    /// <summary>
    /// 获取标准化后的传入请求载荷。
    /// </summary>
    public Dictionary<string, object?>? Payload { get; }

    /// <summary>
    /// 获取发送到上游的请求载荷。
    /// </summary>
    public Dictionary<string, object?>? UpstreamRequest { get; }

    /// <summary>
    /// 获取捕获到的上游响应载荷。
    /// </summary>
    public Dictionary<string, object?>? UpstreamResponse { get; }

    /// <summary>
    /// 获取捕获到的返回给调用方的响应载荷。
    /// </summary>
    public Dictionary<string, object?>? ResponsePayload { get; }

    /// <summary>
    /// 获取返回给调用方的错误响应（如果可用）。
    /// </summary>
    public object? ErrorResponse { get; }

    /// <summary>
    /// 获取调用方请求的模型（如果可用）。
    /// </summary>
    public string? RequestModel { get; }

    /// <summary>
    /// 获取路由到上游提供方的模型（如果可用）。
    /// </summary>
    public string? UpstreamModel { get; }

    /// <summary>
    /// 获取选中的通道标识符（如果可用）。
    /// </summary>
    public string? ChannelId { get; }

    /// <summary>
    /// 获取选中的通道类型（如果可用）。
    /// </summary>
    public string? ChannelType { get; }

    /// <summary>
    /// 获取指示请求是否使用流式响应的值。
    /// </summary>
    public bool IsStream { get; }

    /// <summary>
    /// 获取首令牌时间，单位为毫秒（如果可用）。
    /// </summary>
    public int? TtftMs { get; }

    /// <summary>
    /// 获取响应状态码。
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// 获取总请求耗时，单位为毫秒。
    /// </summary>
    public int DurationMs { get; }

    /// <summary>
    /// 获取错误消息（如果可用）。
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// 获取联网搜索诊断详情（如果可用）。
    /// </summary>
    public Dictionary<string, object?>? WebSearchDetails { get; }

    /// <summary>
    /// 获取传入的超文本传输协议方法。
    /// </summary>
    public string Method { get; }

    /// <summary>
    /// 获取传入请求路径。
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// 获取客户端网络地址（如果可用）。
    /// </summary>
    public string? ClientIp { get; }

    /// <summary>
    /// 获取标准化后的传入请求头。
    /// </summary>
    public IReadOnlyDictionary<string, string> RequestHeaders { get; }

    /// <summary>
    /// 获取请求日志类型。
    /// </summary>
    public string RequestType { get; }

    /// <summary>
    /// 获取父请求日志标识符（如果可用）。
    /// </summary>
    public Guid? ParentRequestLogId { get; }

    /// <summary>
    /// 获取 OCR 专用详情（如果可用）。
    /// </summary>
    public Dictionary<string, object?>? OcrDetails { get; }

    /// <summary>
    /// 获取流式写出时序诊断（如果可用）。
    /// </summary>
    public StreamWriteMetrics? StreamWriteMetrics { get; }

    /// <summary>
    /// 获取按原始 SSE line 记录的上游流片段（如果可用）。
    /// </summary>
    public IReadOnlyList<ProxyRequestStreamLineCapture>? StreamLines { get; }
}

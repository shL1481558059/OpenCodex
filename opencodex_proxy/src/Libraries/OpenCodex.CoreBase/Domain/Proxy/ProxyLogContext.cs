using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// 包含写入请求日志的代理请求值。
/// </summary>
public sealed class ProxyLogContext
{
    /// <summary>
    /// 初始化 <see cref="ProxyLogContext"/> 类的新实例。
    /// </summary>
    /// <param name="RequestId">唯一请求标识符。</param>
    /// <param name="OwnerUsername">拥有该请求的用户名。</param>
    /// <param name="ApiKeyId">请求使用的访问密钥标识符（如果可用）。</param>
    /// <param name="Payload">标准化后的传入请求载荷。</param>
    /// <param name="UpstreamRequest">发送到上游的请求载荷。</param>
    /// <param name="UpstreamResponse">捕获到的上游响应载荷。</param>
    /// <param name="ResponsePayload">捕获到的返回给调用方的响应载荷。</param>
    /// <param name="ErrorResponse">返回给调用方的错误响应（如果可用）。</param>
    /// <param name="RequestModel">调用方请求的模型（如果可用）。</param>
    /// <param name="UpstreamModel">路由到上游提供方的模型（如果可用）。</param>
    /// <param name="ChannelId">选中的通道标识符（如果可用）。</param>
    /// <param name="ChannelType">选中的通道类型（如果可用）。</param>
    /// <param name="IsStream">指示请求是否使用流式响应的值。</param>
    /// <param name="TtftMs">首令牌时间，单位为毫秒（如果可用）。</param>
    /// <param name="StatusCode">响应状态码。</param>
    /// <param name="DurationMs">总请求耗时，单位为毫秒。</param>
    /// <param name="Error">错误消息（如果可用）。</param>
    /// <param name="WebSearchDetails">联网搜索诊断详情（如果可用）。</param>
    /// <param name="RequestType">请求日志类型。</param>
    /// <param name="ParentRequestLogId">父请求日志标识符（如果可用）。</param>
    /// <param name="OcrDetails">OCR 专用详情（如果可用）。</param>
    /// <param name="StreamWriteMetrics">流式写出时序诊断（如果可用）。</param>
    /// <param name="StreamLines">按原始 SSE line 记录的上游流片段（如果可用）。</param>
    public ProxyLogContext(
        string RequestId,
        string OwnerUsername,
        Guid? ApiKeyId,
        Dictionary<string, object?>? Payload,
        Dictionary<string, object?>? UpstreamRequest,
        Dictionary<string, object?>? UpstreamResponse,
        Dictionary<string, object?>? ResponsePayload,
        object? ErrorResponse,
        string? RequestModel,
        string? UpstreamModel,
        string? ChannelId,
        string? ChannelType,
        bool IsStream,
        int? TtftMs,
        int StatusCode,
        int DurationMs,
        string? Error,
        Dictionary<string, object?>? WebSearchDetails,
        string RequestType = ProxyRequestTypes.Main,
        Guid? ParentRequestLogId = null,
        Dictionary<string, object?>? OcrDetails = null,
        StreamWriteMetrics? StreamWriteMetrics = null,
        IReadOnlyList<ProxyRequestStreamLineCapture>? StreamLines = null)
    {
        this.RequestId = RequestId;
        this.OwnerUsername = OwnerUsername;
        this.ApiKeyId = ApiKeyId;
        this.Payload = Payload;
        this.UpstreamRequest = UpstreamRequest;
        this.UpstreamResponse = UpstreamResponse;
        this.ResponsePayload = ResponsePayload;
        this.ErrorResponse = ErrorResponse;
        this.RequestModel = RequestModel;
        this.UpstreamModel = UpstreamModel;
        this.ChannelId = ChannelId;
        this.ChannelType = ChannelType;
        this.IsStream = IsStream;
        this.TtftMs = TtftMs;
        this.StatusCode = StatusCode;
        this.DurationMs = DurationMs;
        this.Error = Error;
        this.WebSearchDetails = WebSearchDetails;
        this.RequestType = RequestType;
        this.ParentRequestLogId = ParentRequestLogId;
        this.OcrDetails = OcrDetails;
        this.StreamWriteMetrics = StreamWriteMetrics;
        this.StreamLines = StreamLines;
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

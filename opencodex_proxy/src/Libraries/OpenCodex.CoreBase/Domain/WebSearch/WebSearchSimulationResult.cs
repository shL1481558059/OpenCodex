namespace OpenCodex.CoreBase.Domain.WebSearch;

/// <summary>
/// 表示非流式联网搜索模拟的完整结果。
/// </summary>
public sealed class WebSearchSimulationResult
{
    /// <summary>
    /// 初始化 <see cref="WebSearchSimulationResult"/> 类的新实例。
    /// </summary>
    /// <param name="finalUpstreamRequest">经过模拟修改后发送到上游的最终请求载荷。</param>
    /// <param name="finalUpstreamResponse">最终上游响应载荷（如果可用）。</param>
    /// <param name="responsePayload">返回给调用方的响应载荷。</param>
    /// <param name="details">模拟诊断详情。</param>
    public WebSearchSimulationResult(
        Dictionary<string, object?> finalUpstreamRequest,
        Dictionary<string, object?>? finalUpstreamResponse,
        Dictionary<string, object?> responsePayload,
        Dictionary<string, object?> details)
    {
        FinalUpstreamRequest = finalUpstreamRequest;
        FinalUpstreamResponse = finalUpstreamResponse;
        ResponsePayload = responsePayload;
        Details = details;
    }

    /// <summary>
    /// 获取或设置经过模拟修改后发送到上游的最终请求载荷。
    /// </summary>
    public Dictionary<string, object?> FinalUpstreamRequest { get; set; }

    /// <summary>
    /// 获取或设置最终上游响应载荷（如果可用）。
    /// </summary>
    public Dictionary<string, object?>? FinalUpstreamResponse { get; set; }

    /// <summary>
    /// 获取或设置返回给调用方的响应载荷。
    /// </summary>
    public Dictionary<string, object?> ResponsePayload { get; set; }

    /// <summary>
    /// 获取或设置模拟诊断详情。
    /// </summary>
    public Dictionary<string, object?> Details { get; set; }
}

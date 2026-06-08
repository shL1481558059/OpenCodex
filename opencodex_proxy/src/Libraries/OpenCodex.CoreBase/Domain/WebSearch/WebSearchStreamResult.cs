namespace OpenCodex.CoreBase.Domain.WebSearch;

/// <summary>
/// 表示流式联网搜索模拟的累计结果。
/// </summary>
public sealed class WebSearchStreamResult
{
    /// <summary>
    /// 获取或设置经过模拟修改后发送到上游的最终请求载荷。
    /// </summary>
    public Dictionary<string, object?>? FinalUpstreamRequest { get; set; }

    /// <summary>
    /// 获取或设置最终上游响应载荷（如果可用）。
    /// </summary>
    public Dictionary<string, object?>? FinalUpstreamResponse { get; set; }

    /// <summary>
    /// 获取或设置为下游调用方累计的响应载荷。
    /// </summary>
    public Dictionary<string, object?>? ResponsePayload { get; set; }

    /// <summary>
    /// 获取或设置流式模拟诊断详情。
    /// </summary>
    public Dictionary<string, object?>? Details { get; set; }
}

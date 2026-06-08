using OpenCodex.CoreBase.Domain.WebSearch;

namespace OpenCodex.CoreBase.Services.WebSearch;

/// <summary>
/// 定义联网搜索模拟器。
/// </summary>
public interface IWebSearchSimulator
{
    /// <summary>
    /// 判断当前请求是否可以执行联网搜索模拟。
    /// </summary>
    /// <param name="entryProtocol">入口协议名称。</param>
    /// <param name="channelType">通道类型。</param>
    /// <param name="ownerRole">访问密钥拥有者角色。</param>
    /// <param name="payload">传入请求载荷。</param>
    /// <returns>如果可以模拟则为 <see langword="true"/>；否则为 <see langword="false"/>。</returns>
    bool CanSimulate(
        string entryProtocol,
        string channelType,
        string ownerRole,
        IReadOnlyDictionary<string, object?> payload);

    /// <summary>
    /// 执行非流式联网搜索模拟。
    /// </summary>
    /// <param name="channel">通道配置。</param>
    /// <param name="upstreamRequest">上游请求内容。</param>
    /// <param name="payload">原始请求载荷。</param>
    /// <param name="originalModel">原始请求模型名称。</param>
    /// <param name="defaultTimeout">默认超时时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>非流式联网搜索模拟结果。</returns>
    Task<WebSearchSimulationResult> RunAsync(
        IReadOnlyDictionary<string, object?> channel,
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> payload,
        string? originalModel,
        int defaultTimeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// 执行流式联网搜索模拟。
    /// </summary>
    /// <param name="channel">通道配置。</param>
    /// <param name="upstreamRequest">上游请求内容。</param>
    /// <param name="payload">原始请求载荷。</param>
    /// <param name="originalModel">原始请求模型名称。</param>
    /// <param name="defaultTimeout">默认超时时间。</param>
    /// <param name="result">用于累计模拟结果的对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐段返回给客户端的流式内容。</returns>
    IAsyncEnumerable<string> RunChatStreamAsync(
        IReadOnlyDictionary<string, object?> channel,
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> payload,
        string? originalModel,
        int defaultTimeout,
        WebSearchStreamResult result,
        CancellationToken cancellationToken);
}

namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// 定义向上游聊天补全提供方发送请求的操作。
/// </summary>
public interface IUpstreamClient
{
    /// <summary>
    /// 向上游提供方发送非流式结构化请求。
    /// </summary>
    /// <param name="channel">用于访问上游提供方的通道配置。</param>
    /// <param name="payload">要发送的请求载荷。</param>
    /// <param name="defaultTimeout">通道未覆盖时使用的默认超时时间，单位为秒。</param>
    /// <param name="cancellationToken">用于取消上游请求的令牌。</param>
    /// <returns>以字典表示的上游 JSON 响应。</returns>
    Task<Dictionary<string, object?>> PostJsonAsync(
        IReadOnlyDictionary<string, object?> channel,
        IReadOnlyDictionary<string, object?> payload,
        int defaultTimeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// 向上游提供方发送流式结构化请求。
    /// </summary>
    /// <param name="channel">用于访问上游提供方的通道配置。</param>
    /// <param name="payload">要发送的请求载荷。</param>
    /// <param name="defaultTimeout">通道未覆盖时使用的默认超时时间，单位为秒。</param>
    /// <param name="cancellationToken">用于取消上游请求的令牌。</param>
    /// <returns>流式响应行的异步序列。</returns>
    IAsyncEnumerable<string> StreamJsonAsync(
        IReadOnlyDictionary<string, object?> channel,
        IReadOnlyDictionary<string, object?> payload,
        int defaultTimeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// 定义从上游提供方获取模型元数据的操作。
/// </summary>
public interface IUpstreamModelClient
{
    /// <summary>
    /// 列出可通过上游提供方通道访问的模型。
    /// </summary>
    /// <param name="channel">用于访问上游提供方的通道配置。</param>
    /// <param name="defaultTimeout">通道未覆盖时使用的默认超时时间，单位为秒。</param>
    /// <param name="cancellationToken">用于取消上游请求的令牌。</param>
    /// <returns>以字典表示的上游模型列表响应。</returns>
    Task<Dictionary<string, object?>> ListModelsAsync(
        IReadOnlyDictionary<string, object?> channel,
        int defaultTimeout,
        CancellationToken cancellationToken);
}

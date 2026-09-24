namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// 承载处理代理请求时使用的基础逐请求默认值。
/// </summary>
public sealed class ProxyRequestState
{
    /// <summary>
    /// 初始化 <see cref="ProxyRequestState"/> 类的新实例。
    /// </summary>
    /// <param name="requestId">唯一请求标识符。</param>
    /// <param name="defaultTimeout">默认上游超时时间，单位为秒。</param>
    public ProxyRequestState(string requestId, int defaultTimeout)
    {
        RequestId = requestId;
        DefaultTimeout = defaultTimeout;
    }

    /// <summary>
    /// 获取唯一请求标识符。
    /// </summary>
    public string RequestId { get; }

    /// <summary>
    /// 获取默认上游超时时间，单位为秒。
    /// </summary>
    public int DefaultTimeout { get; }
}

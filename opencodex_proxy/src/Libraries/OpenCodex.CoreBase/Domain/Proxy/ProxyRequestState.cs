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
    /// <param name="defaultOwnerUsername">未解析到访问密钥所有者时使用的默认所有者用户名。</param>
    /// <param name="defaultTimeout">默认上游超时时间，单位为秒。</param>
    public ProxyRequestState(string requestId, string defaultOwnerUsername, int defaultTimeout)
    {
        RequestId = requestId;
        DefaultOwnerUsername = defaultOwnerUsername;
        DefaultTimeout = defaultTimeout;
    }

    /// <summary>
    /// 获取唯一请求标识符。
    /// </summary>
    public string RequestId { get; }

    /// <summary>
    /// 获取未解析到访问密钥所有者时使用的默认所有者用户名。
    /// </summary>
    public string DefaultOwnerUsername { get; }

    /// <summary>
    /// 获取默认上游超时时间，单位为秒。
    /// </summary>
    public int DefaultTimeout { get; }
}

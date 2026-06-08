using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义非流式代理请求服务。
/// </summary>
public interface IProxyNonStreamService
{
    /// <summary>
    /// 向上游发送非流式代理请求。
    /// </summary>
    /// <param name="context">非流式代理上下文。</param>
    /// <returns>非流式代理结果。</returns>
    Task<ProxyNonStreamResult> SendAsync(ProxyNonStreamContext context);
}

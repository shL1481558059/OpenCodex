using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义代理端点编排服务。
/// </summary>
public interface IProxyEndpointService
{
    /// <summary>
    /// 处理一次完整的代理请求。
    /// </summary>
    /// <param name="context">代理端点上下文。</param>
    /// <returns>代理端点处理结果。</returns>
    Task<ProxyEndpointResult> ProxyAsync(ProxyEndpointContext context);
}

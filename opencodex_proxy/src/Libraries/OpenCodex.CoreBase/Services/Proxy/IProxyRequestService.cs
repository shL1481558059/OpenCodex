using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义代理请求基础处理服务。
/// </summary>
public interface IProxyRequestService
{
    /// <summary>
    /// 创建新的代理请求状态。
    /// </summary>
    /// <returns>代理请求状态。</returns>
    ProxyRequestState StartRequest();
}

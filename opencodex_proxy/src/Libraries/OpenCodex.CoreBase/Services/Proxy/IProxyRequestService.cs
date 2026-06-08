using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs;

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

    /// <summary>
    /// 根据认证头认证代理访问密钥。
    /// </summary>
    /// <param name="authorizationHeader">传入请求中的认证头。</param>
    /// <returns>已认证的访问密钥信息。</returns>
    AuthenticatedAccessApiKeyDto AuthenticateAccessKey(string? authorizationHeader);
}

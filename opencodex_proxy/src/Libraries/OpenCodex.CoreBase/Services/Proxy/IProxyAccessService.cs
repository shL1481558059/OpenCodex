using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义代理访问密钥认证服务。
/// </summary>
public interface IProxyAccessService
{
    /// <summary>
    /// 根据承载认证头认证访问密钥。
    /// </summary>
    /// <param name="authorizationHeader">传入请求中的认证头。</param>
    /// <returns>已认证的访问密钥信息。</returns>
    AuthenticatedAccessApiKeyDto AuthenticateBearer(string? authorizationHeader);
}

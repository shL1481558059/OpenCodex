using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义代理访问密钥认证服务。
/// </summary>
public interface IProxyAccessService
{
    /// <summary>
    /// 根据原始访问密钥认证身份。
    /// </summary>
    /// <param name="rawKey">从请求凭据中提取的原始访问密钥。</param>
    /// <returns>已认证的访问密钥信息；密钥或所有者不可用时为 <see langword="null"/>。</returns>
    Task<AuthenticatedAccessApiKeyDto?> AuthenticateRawKeyAsync(string? rawKey);
}

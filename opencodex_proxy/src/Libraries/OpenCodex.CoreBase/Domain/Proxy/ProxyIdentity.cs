namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// 表示已通过代理访问密钥认证的请求身份。
/// </summary>
public sealed class ProxyIdentity
{
    /// <summary>
    /// 初始化 <see cref="ProxyIdentity"/> 类的新实例。
    /// </summary>
    /// <param name="apiKeyId">访问密钥标识。</param>
    /// <param name="ownerUserId">访问密钥所有者用户标识。</param>
    /// <param name="ownerUsername">访问密钥所有者用户名。</param>
    /// <param name="ownerRole">访问密钥所有者角色。</param>
    public ProxyIdentity(
        Guid apiKeyId,
        Guid ownerUserId,
        string ownerUsername,
        string ownerRole)
    {
        ApiKeyId = apiKeyId;
        OwnerUserId = ownerUserId;
        OwnerUsername = ownerUsername;
        OwnerRole = ownerRole;
    }

    /// <summary>
    /// 获取访问密钥标识。
    /// </summary>
    public Guid ApiKeyId { get; }

    /// <summary>
    /// 获取访问密钥所有者用户标识。
    /// </summary>
    public Guid OwnerUserId { get; }

    /// <summary>
    /// 获取访问密钥所有者用户名。
    /// </summary>
    public string OwnerUsername { get; }

    /// <summary>
    /// 获取访问密钥所有者角色。
    /// </summary>
    public string OwnerRole { get; }
}

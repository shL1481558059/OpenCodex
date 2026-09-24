namespace OpenCodex.Api.Authentication;

/// <summary>
/// 定义代理身份附加在 <see cref="System.Security.Claims.ClaimsPrincipal"/> 上的声明类型。
/// </summary>
/// <remarks>
/// 代理身份刻意不使用 <see cref="System.Security.Claims.ClaimTypes.Role"/>，
/// 避免访问密钥所有者角色被管理端权限判断当作管理员角色读取。
/// </remarks>
public static class ProxyIdentityClaims
{
    /// <summary>
    /// 访问密钥标识声明类型。
    /// </summary>
    public const string ApiKeyId = "opencodex_proxy_api_key_id";

    /// <summary>
    /// 访问密钥所有者用户标识声明类型。
    /// </summary>
    public const string OwnerUserId = "opencodex_proxy_owner_user_id";

    /// <summary>
    /// 访问密钥所有者角色声明类型。
    /// </summary>
    public const string OwnerRole = "opencodex_proxy_owner_role";
}

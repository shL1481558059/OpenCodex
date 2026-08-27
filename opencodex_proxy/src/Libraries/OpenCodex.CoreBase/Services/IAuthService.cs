using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Domain;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义后台认证服务。
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// 认证方案名称。
    /// </summary>
    const string AuthenticationScheme = "OpenCodexAdmin";

    /// <summary>
    /// 认证 Cookie 名称。
    /// </summary>
    const string CookieName = "opencodex_admin_auth";

    /// <summary>
    /// 读取首次初始化状态。
    /// </summary>
    /// <returns>首次初始化状态。</returns>
    ApiOpResult<SetupStateResponse> GetSetupState();

    /// <summary>
    /// 首次初始化超级管理员。
    /// </summary>
    /// <param name="username">管理员用户名。</param>
    /// <param name="password">管理员密码。</param>
    /// <returns>初始化后的会话结果。</returns>
    ApiOpResult<SessionResponse> Initialize(
        string? username,
        string? password);

    /// <summary>
    /// 使用用户名和密码登录。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <param name="password">密码。</param>
    /// <returns>登录后的会话结果。</returns>
    ApiOpResult<SessionResponse> Login(string? username, string? password);

    /// <summary>
    /// 将用户信息写入认证 Cookie。
    /// </summary>
    /// <param name="user">要登录的用户。</param>
    /// <param name="persistentLifetime">Cookie 有效期。</param>
    Task SetUserAsync(SessionUser user, TimeSpan persistentLifetime);

    /// <summary>
    /// 清除当前认证会话。
    /// </summary>
    Task ClearUserAsync();
}

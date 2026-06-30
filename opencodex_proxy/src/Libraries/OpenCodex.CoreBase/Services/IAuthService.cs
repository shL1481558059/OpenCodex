using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义后台认证服务。
/// </summary>
public interface IAuthService
{
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
}

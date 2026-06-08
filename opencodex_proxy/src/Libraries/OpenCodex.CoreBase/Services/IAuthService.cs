using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义后台认证服务。
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// 使用用户名和密码登录。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <param name="password">密码。</param>
    /// <returns>登录后的会话结果。</returns>
    ApiOpResult<SessionResponse> Login(string? username, string? password);
}

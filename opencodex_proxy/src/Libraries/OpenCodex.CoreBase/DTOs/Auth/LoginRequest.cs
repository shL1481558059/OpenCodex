namespace OpenCodex.CoreBase.DTOs.Auth;

/// <summary>
/// 表示管理员登录请求。
/// </summary>
public sealed class LoginRequest
{
    /// <summary>
    /// 获取或设置管理员用户名。
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置管理员密码。
    /// </summary>
    public string Password { get; set; } = string.Empty;
}

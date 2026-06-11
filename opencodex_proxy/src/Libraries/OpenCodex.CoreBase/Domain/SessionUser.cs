namespace OpenCodex.CoreBase.Domain;

/// <summary>
/// 表示当前已认证的管理员用户身份。
/// </summary>
public sealed class SessionUser
{
    /// <summary>
    /// 初始化 <see cref="SessionUser"/> 类的新实例。
    /// </summary>
    /// <param name="username">管理员用户名。</param>
    /// <param name="role">管理员角色。</param>
    /// <param name="enabled">指示管理员用户是否启用的值。</param>
    public SessionUser(string username, string role, bool enabled)
    {
        Username = username;
        Role = role;
        Enabled = enabled;
    }

    /// <summary>
    /// 获取管理员用户名。
    /// </summary>
    public string Username { get; }

    /// <summary>
    /// 获取管理员角色。
    /// </summary>
    public string Role { get; }

    /// <summary>
    /// 获取指示管理员用户是否启用的值。
    /// </summary>
    public bool Enabled { get; }
}

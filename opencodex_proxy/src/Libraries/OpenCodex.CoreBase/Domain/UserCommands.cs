namespace OpenCodex.CoreBase.Domain;

/// <summary>
/// 表示创建管理员用户所需的数据。
/// </summary>
public sealed class UserCreateCommand
{
    /// <summary>
    /// 初始化 <see cref="UserCreateCommand"/> 类的新实例。
    /// </summary>
    /// <param name="username">新管理员用户的用户名。</param>
    /// <param name="password">新管理员用户的密码。</param>
    /// <param name="enabled">指示该用户是否应启用的值。</param>
    public UserCreateCommand(string username, string password, bool enabled)
    {
        Username = username;
        Password = password;
        Enabled = enabled;
    }

    /// <summary>
    /// 获取新管理员用户的用户名。
    /// </summary>
    public string Username { get; }

    /// <summary>
    /// 获取新管理员用户的密码。
    /// </summary>
    public string Password { get; }

    /// <summary>
    /// 获取指示该用户是否应启用的值。
    /// </summary>
    public bool Enabled { get; }
}

/// <summary>
/// 表示更新管理员用户所需的数据。
/// </summary>
public sealed class UserUpdateCommand
{
    /// <summary>
    /// 初始化 <see cref="UserUpdateCommand"/> 类的新实例。
    /// </summary>
    /// <param name="enabled">要应用的启用状态；为 <see langword="null"/> 时保持不变。</param>
    /// <param name="password">要应用的密码；为 <see langword="null"/> 时保持不变。</param>
    public UserUpdateCommand(bool? enabled, string? password)
    {
        Enabled = enabled;
        Password = password;
    }

    /// <summary>
    /// 获取要应用的启用状态；为 <see langword="null"/> 时保持不变。
    /// </summary>
    public bool? Enabled { get; }

    /// <summary>
    /// 获取要应用的密码；为 <see langword="null"/> 时保持不变。
    /// </summary>
    public string? Password { get; }
}

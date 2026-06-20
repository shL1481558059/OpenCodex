namespace OpenCodex.CoreBase.Domain;

/// <summary>
/// 表示当前已认证的管理员用户身份。
/// </summary>
public sealed class SessionUser
{
    public SessionUser(Guid userId, string username, string role, bool enabled)
    {
        UserId = userId;
        Username = username;
        Role = role;
        Enabled = enabled;
    }

    public Guid UserId { get; }

    public string Username { get; }

    public string Role { get; }

    public bool Enabled { get; }
}

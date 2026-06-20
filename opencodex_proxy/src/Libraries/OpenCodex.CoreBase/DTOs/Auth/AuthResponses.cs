using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.Auth;

/// <summary>
/// 表示管理员会话状态响应。
/// </summary>
public sealed class SessionResponse
{
    /// <summary>
    /// 初始化 <see cref="SessionResponse"/> 类的新实例。
    /// </summary>
    /// <param name="authenticated">指示当前会话是否已认证的值。</param>
    /// <param name="user">当前会话用户信息；未登录时为 <see langword="null"/>。</param>
    public SessionResponse(bool authenticated, SessionUserResponse? user)
    {
        Authenticated = authenticated;
        User = user;
    }

    /// <summary>
    /// 获取指示当前会话是否已认证的值。
    /// </summary>
    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; }

    /// <summary>
    /// 获取当前会话用户信息；未登录时为 <see langword="null"/>。
    /// </summary>
    [JsonPropertyName("user")]
    public SessionUserResponse? User { get; }

    /// <summary>
    /// 根据已认证管理员用户信息创建会话响应。
    /// </summary>
    /// <param name="username">管理员用户名。</param>
    /// <param name="role">管理员角色。</param>
    /// <param name="enabled">指示管理员用户是否启用的值。</param>
    /// <returns>已认证的会话响应。</returns>
    public static SessionResponse From(
        Guid userId,
        string username,
        string role,
        bool enabled)
    {
        return new SessionResponse(
            true,
            new SessionUserResponse(userId, username, role, enabled));
    }

    /// <summary>
    /// 创建未登录状态的会话响应。
    /// </summary>
    /// <returns>未认证的会话响应。</returns>
    public static SessionResponse LoggedOut()
    {
        return new SessionResponse(false, null);
    }
}

/// <summary>
/// 表示管理员会话中的用户信息。
/// </summary>
public sealed class SessionUserResponse
{
    /// <summary>
    /// 初始化 <see cref="SessionUserResponse"/> 类的新实例。
    /// </summary>
    /// <param name="username">管理员用户名。</param>
    /// <param name="role">管理员角色。</param>
    /// <param name="enabled">指示管理员用户是否启用的值。</param>
    public SessionUserResponse(Guid userId, string username, string role, bool enabled)
    {
        UserId = userId;
        Username = username;
        Role = role;
        Enabled = enabled;
    }

    /// <summary>
    /// 获取管理员用户标识。
    /// </summary>
    [JsonPropertyName("user_id")]
    public Guid UserId { get; }

    /// <summary>
    /// 获取管理员用户名。
    /// </summary>
    [JsonPropertyName("username")]
    public string Username { get; }

    /// <summary>
    /// 获取管理员角色。
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; }

    /// <summary>
    /// 获取指示管理员用户是否启用的值。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; }
}

using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.DTOs.Users;

/// <summary>
/// 表示管理员用户列表响应。
/// </summary>
public sealed class UsersResponse
{
    /// <summary>
    /// 初始化 <see cref="UsersResponse"/> 类的新实例。
    /// </summary>
    /// <param name="users">管理员用户响应列表。</param>
    public UsersResponse(IReadOnlyList<UserResponse> users)
    {
        Users = users;
    }

    /// <summary>
    /// 获取管理员用户响应列表。
    /// </summary>
    [JsonPropertyName("users")]
    public IReadOnlyList<UserResponse> Users { get; }

    /// <summary>
    /// 根据用户 DTO 列表创建管理员用户列表响应。
    /// </summary>
    /// <param name="users">用户 DTO 列表；为 <see langword="null"/> 时按空列表处理。</param>
    /// <returns>管理员用户列表响应。</returns>
    public static UsersResponse From(IReadOnlyList<UserDto>? users)
    {
        return new UsersResponse(users?.Select(UserResponse.From).ToList() ?? []);
    }
}

/// <summary>
/// 表示单个管理员用户响应载荷。
/// </summary>
public sealed class UserResponsePayload
{
    /// <summary>
    /// 初始化 <see cref="UserResponsePayload"/> 类的新实例。
    /// </summary>
    /// <param name="user">管理员用户响应。</param>
    public UserResponsePayload(UserResponse user)
    {
        User = user;
    }

    /// <summary>
    /// 获取管理员用户响应。
    /// </summary>
    [JsonPropertyName("user")]
    public UserResponse User { get; }

    /// <summary>
    /// 根据用户 DTO 创建单个管理员用户响应载荷。
    /// </summary>
    /// <param name="user">用户 DTO。</param>
    /// <returns>单个管理员用户响应载荷。</returns>
    public static UserResponsePayload From(UserDto user)
    {
        return new UserResponsePayload(UserResponse.From(user));
    }
}

/// <summary>
/// 表示删除管理员用户的响应。
/// </summary>
public sealed class DeleteUserResponse
{
    /// <summary>
    /// 初始化 <see cref="DeleteUserResponse"/> 类的新实例。
    /// </summary>
    /// <param name="deleted">指示管理员用户是否已删除的值。</param>
    /// <param name="user">被删除的管理员用户响应。</param>
    public DeleteUserResponse(bool deleted, UserResponse user)
    {
        Deleted = deleted;
        User = user;
    }

    /// <summary>
    /// 获取指示管理员用户是否已删除的值。
    /// </summary>
    [JsonPropertyName("deleted")]
    public bool Deleted { get; }

    /// <summary>
    /// 获取被删除的管理员用户响应。
    /// </summary>
    [JsonPropertyName("user")]
    public UserResponse User { get; }

    /// <summary>
    /// 根据用户 DTO 创建删除管理员用户响应。
    /// </summary>
    /// <param name="user">被删除的用户 DTO。</param>
    /// <returns>删除管理员用户响应。</returns>
    public static DeleteUserResponse From(UserDto user)
    {
        return new DeleteUserResponse(true, UserResponse.From(user));
    }
}

/// <summary>
/// 表示管理员用户响应。
/// </summary>
public sealed class UserResponse
{
    /// <summary>
    /// 初始化 <see cref="UserResponse"/> 类的新实例。
    /// </summary>
    /// <param name="username">管理员用户名。</param>
    /// <param name="role">管理员角色。</param>
    /// <param name="enabled">指示管理员用户是否启用的值。</param>
    /// <param name="createdAt">创建时间戳。</param>
    /// <param name="updatedAt">最后更新时间戳。</param>
    public UserResponse(
        string username,
        string role,
        bool enabled,
        double createdAt,
        double updatedAt)
    {
        Username = username;
        Role = role;
        Enabled = enabled;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

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

    /// <summary>
    /// 获取创建时间戳。
    /// </summary>
    [JsonPropertyName("created_at")]
    public double CreatedAt { get; }

    /// <summary>
    /// 获取最后更新时间戳。
    /// </summary>
    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; }

    /// <summary>
    /// 根据用户 DTO 创建管理员用户响应。
    /// </summary>
    /// <param name="user">用户 DTO。</param>
    /// <returns>管理员用户响应。</returns>
    public static UserResponse From(UserDto user)
    {
        return new UserResponse(
            user.Username,
            user.Role,
            user.Enabled,
            user.CreatedAt,
            user.UpdatedAt);
    }
}

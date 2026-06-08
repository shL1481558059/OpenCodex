using System.Text.Json.Serialization;
using OpenCodex.CoreBase.Domain;

namespace OpenCodex.CoreBase.DTOs.Users;

/// <summary>
/// 表示创建管理员用户的请求。
/// </summary>
public sealed class UserCreateRequest
{
    /// <summary>
    /// 获取或设置管理员用户名。
    /// </summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置管理员密码。
    /// </summary>
    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置指示管理员用户是否启用的值；为空时默认启用。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// 将请求转换为创建管理员用户命令。
    /// </summary>
    /// <returns>创建管理员用户命令。</returns>
    public UserCreateCommand ToCommand()
    {
        return new UserCreateCommand(
            Username,
            Password,
            Enabled is not false);
    }
}

/// <summary>
/// 表示更新管理员用户的请求。
/// </summary>
public sealed class UserUpdateRequest
{
    /// <summary>
    /// 获取或设置要应用的启用状态；为 <see langword="null"/> 时保持不变。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// 获取或设置要应用的新密码；为 <see langword="null"/> 时保持不变。
    /// </summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>
    /// 将请求转换为更新管理员用户命令。
    /// </summary>
    /// <returns>更新管理员用户命令。</returns>
    public UserUpdateCommand ToCommand()
    {
        return new UserUpdateCommand(Enabled, Password);
    }
}

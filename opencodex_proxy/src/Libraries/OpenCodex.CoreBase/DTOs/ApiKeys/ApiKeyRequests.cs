using System.Text.Json.Serialization;
using OpenCodex.CoreBase.Domain;

namespace OpenCodex.CoreBase.DTOs.ApiKeys;

/// <summary>
/// 表示创建管理员 API 密钥的请求。
/// </summary>
public sealed class ApiKeyCreateRequest
{
    /// <summary>
    /// 获取或设置将拥有该 API 密钥的用户标识符。
    /// </summary>
    [JsonPropertyName("owner_user_id")]
    public Guid? OwnerUserId { get; set; }

    /// <summary>
    /// 获取或设置 API 密钥显示名称。
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 将请求转换为创建 API 密钥命令。
    /// </summary>
    /// <returns>创建 API 密钥命令。</returns>
    public ApiKeyCreateCommand ToCommand()
    {
        return new ApiKeyCreateCommand(OwnerUserId ?? Guid.Empty, Name);
    }
}

/// <summary>
/// 表示更新管理员 API 密钥的请求。
/// </summary>
public sealed class ApiKeyUpdateRequest
{
    /// <summary>
    /// 获取或设置指示 API 密钥是否启用的值。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// 将请求转换为更新 API 密钥命令。
    /// </summary>
    /// <returns>更新 API 密钥命令。</returns>
    public ApiKeyUpdateCommand ToCommand()
    {
        return new ApiKeyUpdateCommand(Enabled);
    }
}

using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.DTOs.ApiKeys;

/// <summary>
/// 表示 API 密钥列表响应。
/// </summary>
public sealed class ApiKeysResponse
{
    /// <summary>
    /// 初始化 <see cref="ApiKeysResponse"/> 类的新实例。
    /// </summary>
    /// <param name="keys">API 密钥响应列表。</param>
    public ApiKeysResponse(IReadOnlyList<AccessApiKeyResponse> keys)
    {
        Keys = keys;
    }

    /// <summary>
    /// 获取 API 密钥响应列表。
    /// </summary>
    [JsonPropertyName("keys")]
    public IReadOnlyList<AccessApiKeyResponse> Keys { get; }

    /// <summary>
    /// 根据访问密钥 DTO 列表创建响应。
    /// </summary>
    /// <param name="keys">访问密钥 DTO 列表；为 <see langword="null"/> 时按空列表处理。</param>
    /// <returns>API 密钥列表响应。</returns>
    public static ApiKeysResponse From(IReadOnlyList<AccessApiKeyDto>? keys)
    {
        return new ApiKeysResponse(
            keys?.Select(AccessApiKeyResponse.From).ToList() ?? []);
    }
}

/// <summary>
/// 表示单个 API 密钥响应载荷。
/// </summary>
public sealed class ApiKeyResponsePayload
{
    /// <summary>
    /// 初始化 <see cref="ApiKeyResponsePayload"/> 类的新实例。
    /// </summary>
    /// <param name="key">API 密钥响应。</param>
    public ApiKeyResponsePayload(AccessApiKeyResponse key)
    {
        Key = key;
    }

    /// <summary>
    /// 获取 API 密钥响应。
    /// </summary>
    [JsonPropertyName("key")]
    public AccessApiKeyResponse Key { get; }

    /// <summary>
    /// 根据访问密钥 DTO 创建单个 API 密钥响应载荷。
    /// </summary>
    /// <param name="key">访问密钥 DTO。</param>
    /// <returns>单个 API 密钥响应载荷。</returns>
    public static ApiKeyResponsePayload From(AccessApiKeyDto key)
    {
        return new ApiKeyResponsePayload(AccessApiKeyResponse.From(key));
    }
}

/// <summary>
/// 表示访问 API 密钥响应。
/// </summary>
public sealed class AccessApiKeyResponse
{
    /// <summary>
    /// 初始化 <see cref="AccessApiKeyResponse"/> 类的新实例。
    /// </summary>
    /// <param name="id">访问密钥的数据库标识符。</param>
    /// <param name="ownerUsername">拥有该访问密钥的用户名。</param>
    /// <param name="name">访问密钥显示名称。</param>
    /// <param name="keyPrefix">可见的密钥前缀。</param>
    /// <param name="keySuffix">可见的密钥后缀。</param>
    /// <param name="maskedKey">密钥的掩码表示。</param>
    /// <param name="enabled">指示访问密钥是否启用的值。</param>
    /// <param name="createdAt">创建时间戳。</param>
    /// <param name="updatedAt">最后更新时间戳。</param>
    /// <param name="lastUsedAt">最后使用时间戳（如果可用）。</param>
    /// <param name="key">完整密钥值（如果可用）。</param>
    public AccessApiKeyResponse(
        long id,
        string ownerUsername,
        string name,
        string keyPrefix,
        string keySuffix,
        string maskedKey,
        bool enabled,
        double createdAt,
        double updatedAt,
        double? lastUsedAt,
        string? key)
    {
        Id = id;
        OwnerUsername = ownerUsername;
        Name = name;
        KeyPrefix = keyPrefix;
        KeySuffix = keySuffix;
        MaskedKey = maskedKey;
        Enabled = enabled;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        LastUsedAt = lastUsedAt;
        Key = key;
    }

    /// <summary>
    /// 获取访问密钥的数据库标识符。
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; }

    /// <summary>
    /// 获取拥有该访问密钥的用户名。
    /// </summary>
    [JsonPropertyName("owner_username")]
    public string OwnerUsername { get; }

    /// <summary>
    /// 获取访问密钥显示名称。
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; }

    /// <summary>
    /// 获取可见的密钥前缀。
    /// </summary>
    [JsonPropertyName("key_prefix")]
    public string KeyPrefix { get; }

    /// <summary>
    /// 获取可见的密钥后缀。
    /// </summary>
    [JsonPropertyName("key_suffix")]
    public string KeySuffix { get; }

    /// <summary>
    /// 获取密钥的掩码表示。
    /// </summary>
    [JsonPropertyName("masked_key")]
    public string MaskedKey { get; }

    /// <summary>
    /// 获取指示访问密钥是否启用的值。
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
    /// 获取最后使用时间戳（如果可用）。
    /// </summary>
    [JsonPropertyName("last_used_at")]
    public double? LastUsedAt { get; }

    /// <summary>
    /// 获取完整密钥值（如果可用）。
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; }

    /// <summary>
    /// 根据访问密钥 DTO 创建响应对象。
    /// </summary>
    /// <param name="key">访问密钥 DTO。</param>
    /// <returns>访问 API 密钥响应。</returns>
    public static AccessApiKeyResponse From(AccessApiKeyDto key)
    {
        return new AccessApiKeyResponse(
            key.Id,
            key.OwnerUsername,
            key.Name,
            key.KeyPrefix,
            key.KeySuffix,
            key.MaskedKey,
            key.Enabled,
            key.CreatedAt,
            key.UpdatedAt,
            key.LastUsedAt,
            key.Key);
    }
}

/// <summary>
/// 表示删除 API 密钥的响应。
/// </summary>
public sealed class DeleteApiKeyResponse
{
    /// <summary>
    /// 初始化 <see cref="DeleteApiKeyResponse"/> 类的新实例。
    /// </summary>
    /// <param name="deleted">指示 API 密钥是否已删除的值。</param>
    public DeleteApiKeyResponse(bool deleted)
    {
        Deleted = deleted;
    }

    /// <summary>
    /// 获取指示 API 密钥是否已删除的值。
    /// </summary>
    [JsonPropertyName("deleted")]
    public bool Deleted { get; }
}

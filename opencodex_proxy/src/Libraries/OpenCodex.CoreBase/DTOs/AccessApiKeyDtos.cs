namespace OpenCodex.CoreBase.DTOs;

/// <summary>
/// 表示管理接口返回的访问密钥。
/// </summary>
/// <param name="id">访问密钥的数据库标识符。</param>
/// <param name="ownerUsername">拥有该访问密钥的用户名。</param>
/// <param name="name">访问密钥的显示名称。</param>
/// <param name="keyPrefix">可见的密钥前缀。</param>
/// <param name="keySuffix">可见的密钥后缀。</param>
/// <param name="maskedKey">密钥的掩码表示。</param>
/// <param name="enabled">指示访问密钥是否启用的值。</param>
/// <param name="createdAt">创建时间戳。</param>
/// <param name="updatedAt">最后更新时间戳。</param>
/// <param name="lastUsedAt">最后使用时间戳（如果可用）。</param>
/// <param name="key">创建后返回的完整密钥值（如果可用）。</param>
public sealed class AccessApiKeyDto(
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
    /// <summary>
    /// 获取访问密钥的数据库标识符。
    /// </summary>
    public long Id { get; } = id;

    /// <summary>
    /// 获取拥有该访问密钥的用户名。
    /// </summary>
    public string OwnerUsername { get; } = ownerUsername;

    /// <summary>
    /// 获取访问密钥的显示名称。
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// 获取可见的密钥前缀。
    /// </summary>
    public string KeyPrefix { get; } = keyPrefix;

    /// <summary>
    /// 获取可见的密钥后缀。
    /// </summary>
    public string KeySuffix { get; } = keySuffix;

    /// <summary>
    /// 获取密钥的掩码表示。
    /// </summary>
    public string MaskedKey { get; } = maskedKey;

    /// <summary>
    /// 获取指示访问密钥是否启用的值。
    /// </summary>
    public bool Enabled { get; } = enabled;

    /// <summary>
    /// 获取创建时间戳。
    /// </summary>
    public double CreatedAt { get; } = createdAt;

    /// <summary>
    /// 获取最后更新时间戳。
    /// </summary>
    public double UpdatedAt { get; } = updatedAt;

    /// <summary>
    /// 获取最后使用时间戳（如果可用）。
    /// </summary>
    public double? LastUsedAt { get; } = lastUsedAt;

    /// <summary>
    /// 获取创建后返回的完整密钥值（如果可用）。
    /// </summary>
    public string? Key { get; } = key;
}

/// <summary>
/// 表示与已认证访问密钥关联的用户。
/// </summary>
/// <param name="username">与密钥关联的用户名。</param>
/// <param name="role">与用户关联的角色。</param>
/// <param name="enabled">指示用户是否启用的值。</param>
public sealed class AccessApiKeyUserDto(
    string username,
    string role,
    bool enabled)
{
    /// <summary>
    /// 获取与密钥关联的用户名。
    /// </summary>
    public string Username { get; } = username;

    /// <summary>
    /// 获取与用户关联的角色。
    /// </summary>
    public string Role { get; } = role;

    /// <summary>
    /// 获取指示用户是否启用的值。
    /// </summary>
    public bool Enabled { get; } = enabled;
}

/// <summary>
/// 表示访问密钥及其已认证用户上下文。
/// </summary>
/// <param name="id">访问密钥的数据库标识符。</param>
/// <param name="ownerUsername">拥有该访问密钥的用户名。</param>
/// <param name="name">访问密钥的显示名称。</param>
/// <param name="keyPrefix">可见的密钥前缀。</param>
/// <param name="keySuffix">可见的密钥后缀。</param>
/// <param name="maskedKey">密钥的掩码表示。</param>
/// <param name="enabled">指示访问密钥是否启用的值。</param>
/// <param name="createdAt">创建时间戳。</param>
/// <param name="updatedAt">最后更新时间戳。</param>
/// <param name="lastUsedAt">最后使用时间戳（如果可用）。</param>
/// <param name="user">与密钥关联的已认证用户。</param>
public sealed class AuthenticatedAccessApiKeyDto(
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
    AccessApiKeyUserDto user)
{
    /// <summary>
    /// 获取访问密钥的数据库标识符。
    /// </summary>
    public long Id { get; } = id;

    /// <summary>
    /// 获取拥有该访问密钥的用户名。
    /// </summary>
    public string OwnerUsername { get; } = ownerUsername;

    /// <summary>
    /// 获取访问密钥的显示名称。
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// 获取可见的密钥前缀。
    /// </summary>
    public string KeyPrefix { get; } = keyPrefix;

    /// <summary>
    /// 获取可见的密钥后缀。
    /// </summary>
    public string KeySuffix { get; } = keySuffix;

    /// <summary>
    /// 获取密钥的掩码表示。
    /// </summary>
    public string MaskedKey { get; } = maskedKey;

    /// <summary>
    /// 获取指示访问密钥是否启用的值。
    /// </summary>
    public bool Enabled { get; } = enabled;

    /// <summary>
    /// 获取创建时间戳。
    /// </summary>
    public double CreatedAt { get; } = createdAt;

    /// <summary>
    /// 获取最后更新时间戳。
    /// </summary>
    public double UpdatedAt { get; } = updatedAt;

    /// <summary>
    /// 获取最后使用时间戳（如果可用）。
    /// </summary>
    public double? LastUsedAt { get; } = lastUsedAt;

    /// <summary>
    /// 获取与密钥关联的已认证用户。
    /// </summary>
    public AccessApiKeyUserDto User { get; } = user;
}

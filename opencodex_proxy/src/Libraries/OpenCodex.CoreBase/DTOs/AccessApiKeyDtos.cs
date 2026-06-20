namespace OpenCodex.CoreBase.DTOs;

/// <summary>
/// 表示管理接口返回的访问密钥。
/// </summary>
public sealed class AccessApiKeyDto(
    Guid id,
    Guid ownerUserId,
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
    public Guid Id { get; } = id;

    public Guid OwnerUserId { get; } = ownerUserId;

    public string OwnerUsername { get; } = ownerUsername;

    public string Name { get; } = name;

    public string KeyPrefix { get; } = keyPrefix;

    public string KeySuffix { get; } = keySuffix;

    public string MaskedKey { get; } = maskedKey;

    public bool Enabled { get; } = enabled;

    public double CreatedAt { get; } = createdAt;

    public double UpdatedAt { get; } = updatedAt;

    public double? LastUsedAt { get; } = lastUsedAt;

    public string? Key { get; } = key;
}

/// <summary>
/// 表示与已认证访问密钥关联的用户。
/// </summary>
public sealed class AccessApiKeyUserDto(
    Guid userId,
    string username,
    string role,
    bool enabled)
{
    public Guid UserId { get; } = userId;

    public string Username { get; } = username;

    public string Role { get; } = role;

    public bool Enabled { get; } = enabled;
}

/// <summary>
/// 表示访问密钥及其已认证用户上下文。
/// </summary>
public sealed class AuthenticatedAccessApiKeyDto(
    Guid id,
    Guid ownerUserId,
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
    public Guid Id { get; } = id;

    public Guid OwnerUserId { get; } = ownerUserId;

    public string OwnerUsername { get; } = ownerUsername;

    public string Name { get; } = name;

    public string KeyPrefix { get; } = keyPrefix;

    public string KeySuffix { get; } = keySuffix;

    public string MaskedKey { get; } = maskedKey;

    public bool Enabled { get; } = enabled;

    public double CreatedAt { get; } = createdAt;

    public double UpdatedAt { get; } = updatedAt;

    public double? LastUsedAt { get; } = lastUsedAt;

    public AccessApiKeyUserDto User { get; } = user;
}

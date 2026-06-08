namespace OpenCodex.CoreBase.DTOs;

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
    public long Id { get; } = id;

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

public sealed class AccessApiKeyUserDto(
    string username,
    string role,
    bool enabled)
{
    public string Username { get; } = username;

    public string Role { get; } = role;

    public bool Enabled { get; } = enabled;
}

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
    public long Id { get; } = id;

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

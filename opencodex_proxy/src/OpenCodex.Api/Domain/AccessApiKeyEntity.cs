namespace OpenCodex.Api.Domain;

public sealed class AccessApiKeyEntity : BaseEntity<long>
{
    public AccessApiKeyEntity(
        long id,
        string ownerUsername,
        string name,
        string keyHash,
        string? keyPlaintext,
        string keyPrefix,
        string keySuffix,
        bool enabled,
        double createdAt,
        double updatedAt,
        double? lastUsedAt)
    {
        Id = id;
        OwnerUsername = ownerUsername;
        Name = name;
        KeyHash = keyHash;
        KeyPlaintext = keyPlaintext;
        KeyPrefix = keyPrefix;
        KeySuffix = keySuffix;
        Enabled = enabled;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        LastUsedAt = lastUsedAt;
    }

    public string OwnerUsername { get; init; }

    public string Name { get; init; }

    public string KeyHash { get; init; }

    public string? KeyPlaintext { get; init; }

    public string KeyPrefix { get; init; }

    public string KeySuffix { get; init; }

    public bool Enabled { get; init; }

    public double CreatedAt { get; init; }

    public double UpdatedAt { get; init; }

    public double? LastUsedAt { get; init; }

    public AccessApiKeyRecord ToRecord(bool includePlaintext)
    {
        return new AccessApiKeyRecord(
            Id,
            OwnerUsername,
            Name,
            KeyPrefix,
            KeySuffix,
            $"{KeyPrefix}...{KeySuffix}",
            Enabled,
            CreatedAt,
            UpdatedAt,
            LastUsedAt,
            includePlaintext ? KeyPlaintext : null);
    }
}

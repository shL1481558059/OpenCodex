namespace OpenCodex.Core.Domain;

public sealed class AccessApiKey : BaseEntity<long>
{
    public string OwnerUsername { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string KeyHash { get; set; } = string.Empty;

    public string? KeyPlaintext { get; set; }

    public string KeyPrefix { get; set; } = string.Empty;

    public string KeySuffix { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }

    public double? LastUsedAt { get; set; }

    public User? Owner { get; set; }

}

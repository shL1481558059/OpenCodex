namespace OpenCodex.Api.Domain;

public sealed class WebSearchSettingsEntity : BaseEntity<long>
{
    public WebSearchSettingsEntity(
        long id,
        bool enabled,
        int keyUsageLimit,
        double createdAt,
        double updatedAt)
    {
        Id = id;
        Enabled = enabled;
        KeyUsageLimit = keyUsageLimit;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public bool Enabled { get; init; }

    public int KeyUsageLimit { get; init; }

    public double CreatedAt { get; init; }

    public double UpdatedAt { get; init; }
}

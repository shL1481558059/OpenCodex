namespace OpenCodex.Api.Domain;

public sealed class TavilyKeyEntity : BaseEntity<long>
{
    public TavilyKeyEntity(
        long id,
        int position,
        string provider,
        string key,
        bool enabled,
        int usageCount,
        int usageLimit,
        double createdAt,
        double updatedAt)
    {
        Id = id;
        Position = position;
        Provider = provider;
        Key = key;
        Enabled = enabled;
        UsageCount = usageCount;
        UsageLimit = usageLimit;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public int Position { get; init; }

    public string Provider { get; init; }

    public string Key { get; init; }

    public bool Enabled { get; init; }

    public int UsageCount { get; init; }

    public int UsageLimit { get; init; }

    public double CreatedAt { get; init; }

    public double UpdatedAt { get; init; }

    public int KeyUsageLimit => UsageLimit;

    public TavilyKeyRecord ToRecord()
    {
        return new TavilyKeyRecord(
            Id,
            Position,
            Provider,
            Key,
            Enabled,
            UsageCount,
            UsageLimit,
            KeyUsageLimit);
    }
}

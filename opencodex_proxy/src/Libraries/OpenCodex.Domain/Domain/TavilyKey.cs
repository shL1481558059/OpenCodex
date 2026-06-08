namespace OpenCodex.Core.Domain;

public sealed class TavilyKey : BaseEntity<long>
{
    public int Position { get; set; }

    public string Provider { get; set; } = "tavily";

    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int UsageCount { get; set; }

    public int UsageLimit { get; set; }

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }

    public int KeyUsageLimit => UsageLimit;

}

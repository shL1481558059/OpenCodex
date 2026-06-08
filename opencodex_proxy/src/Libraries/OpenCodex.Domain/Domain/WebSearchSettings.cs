namespace OpenCodex.Core.Domain;

public sealed class WebSearchSettings : BaseEntity<long>
{
    public bool Enabled { get; set; }

    public int KeyUsageLimit { get; set; }

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }
}

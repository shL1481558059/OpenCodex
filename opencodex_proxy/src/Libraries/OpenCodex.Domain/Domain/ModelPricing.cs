namespace OpenCodex.Core.Domain;

public sealed class ModelPricing : BaseEntity<long>
{
    public string ModelId { get; set; } = string.Empty;

    public string Vendor { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string MatchPattern { get; set; } = string.Empty;

    public double InputPrice { get; set; }

    public double? CachedInputPrice { get; set; }

    public double OutputPrice { get; set; }

    public bool Enabled { get; set; } = true;

    public string Source { get; set; } = string.Empty;

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }
}

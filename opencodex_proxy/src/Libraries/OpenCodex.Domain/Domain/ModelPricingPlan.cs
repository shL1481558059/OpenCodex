namespace OpenCodex.Core.Domain;

public sealed class ModelPricingPlan : BaseEntity<Guid>
{
    public Guid ModelInfoId { get; set; }

    public Guid? ChannelId { get; set; }

    public string Currency { get; set; } = "USD";

    public bool Enabled { get; set; } = true;

    public string Source { get; set; } = string.Empty;

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }
}

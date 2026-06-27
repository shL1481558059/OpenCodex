namespace OpenCodex.Core.Domain;

public sealed class ChannelModelMapping : BaseEntity<Guid>
{
    public Guid ChannelId { get; set; }

    public int Position { get; set; }

    public string RequestModel { get; set; } = string.Empty;

    public string UpstreamModel { get; set; } = string.Empty;

    public bool SupportsImage { get; set; }

    public Guid? ModelInfoId { get; set; }

    public string PricingMode { get; set; } = ChannelModelPricingModes.InheritGlobal;

    public Guid? PricingPlanId { get; set; }

    public bool Enabled { get; set; } = true;

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }
}

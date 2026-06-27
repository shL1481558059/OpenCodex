namespace OpenCodex.Core.Domain;

public sealed class ModelPricingRule : BaseEntity<Guid>
{
    public Guid PricingPlanId { get; set; }

    public string BillingItem { get; set; } = ModelBillingItems.Input;

    public string BillingMode { get; set; } = ModelBillingModes.PerMillionTokens;

    public decimal UnitPrice { get; set; }

    public string TiersJson { get; set; } = "[]";

    public bool Enabled { get; set; } = true;
}

namespace OpenCodex.Core.Domain;

public sealed class ModelPricingRule : BaseEntity<Guid>
{
    public Guid PricingPlanId { get; set; }

    public string BillingItem { get; set; } = ModelBillingItems.Input;

    public string BillingMode { get; set; } = ModelBillingModes.PerMillionTokens;

    public decimal UnitPrice { get; set; }

    public string TiersJson { get; set; } = "[]";

    /// <summary>该计费项是否参与峰谷；为 false 时谷段同样使用基础单价。</summary>
    public bool OffPeakEnabled { get; set; }

    /// <summary>谷段单价；仅在 <see cref="OffPeakEnabled"/> 为 true 且请求落在谷段时生效。</summary>
    public decimal OffPeakUnitPrice { get; set; }

    /// <summary>谷段阶梯定义，结构与 <see cref="TiersJson"/> 一致。</summary>
    public string OffPeakTiersJson { get; set; } = "[]";

    public bool Enabled { get; set; } = true;
}

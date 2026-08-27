namespace OpenCodex.Core.Domain;

public sealed class ModelPricingPlan : BaseEntity<Guid>
{
    public Guid? ModelInfoId { get; set; }

    public Guid? ChannelModelInfoId { get; set; }

    public Guid? ChannelId { get; set; }

    public string Currency { get; set; } = "USD";

    /// <summary>峰谷计费使用的 IANA 时区 ID；空字符串表示未启用峰谷。</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>规范化后的谷段时间窗口数组（不跨午夜）；空数组表示未启用峰谷。</summary>
    public string OffPeakWindowsJson { get; set; } = "[]";

    public bool Enabled { get; set; } = true;

    public string Source { get; set; } = string.Empty;

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }
}

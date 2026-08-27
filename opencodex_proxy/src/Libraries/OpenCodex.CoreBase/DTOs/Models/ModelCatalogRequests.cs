using System.Text.Json.Serialization;
using OpenCodex.CoreBase.Domain.Models;

namespace OpenCodex.CoreBase.DTOs.Models;

public sealed class ModelPricingTierRequest
{
    [JsonPropertyName("up_to")]
    public long? UpTo { get; set; }

    [JsonPropertyName("unit_price")]
    public decimal UnitPrice { get; set; }
}

public sealed class ModelPricingRuleRequest
{
    [JsonPropertyName("billing_item")]
    public string BillingItem { get; set; } = string.Empty;

    [JsonPropertyName("billing_mode")]
    public string BillingMode { get; set; } = string.Empty;

    [JsonPropertyName("unit_price")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("tiers")]
    public List<ModelPricingTierRequest> Tiers { get; set; } = [];

    /// 该计费项是否参与峰谷；为 false 时谷段同样使用基础单价。
    [JsonPropertyName("off_peak_enabled")]
    public bool OffPeakEnabled { get; set; }

    [JsonPropertyName("off_peak_unit_price")]
    public decimal OffPeakUnitPrice { get; set; }

    [JsonPropertyName("off_peak_tiers")]
    public List<ModelPricingTierRequest> OffPeakTiers { get; set; } = [];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed class ModelPricingPlanRequest
{
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    /// 峰谷判定使用的 IANA 时区 ID；留空表示不启用峰谷。
    [JsonPropertyName("time_zone")]
    public string TimeZone { get; set; } = string.Empty;

    /// 谷段窗口；留空表示不启用峰谷。窗口外一律按峰价计费。
    [JsonPropertyName("off_peak_windows")]
    public List<PricingOffPeakWindow> OffPeakWindows { get; set; } = [];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("rules")]
    public List<ModelPricingRuleRequest> Rules { get; set; } = [];
}

public class ModelInfoCreateRequest
{
    [JsonPropertyName("provider_code")]
    public string ProviderCode { get; set; } = string.Empty;

    [JsonPropertyName("provider_id")]
    public Guid? ProviderId { get; set; }

    [JsonPropertyName("model_key")]
    public string ModelKey { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("match_type")]
    public string MatchType { get; set; } = string.Empty;

    [JsonPropertyName("match_pattern")]
    public string MatchPattern { get; set; } = string.Empty;

    [JsonPropertyName("catalog")]
    public Dictionary<string, object?> Catalog { get; set; } = [];

    [JsonPropertyName("capabilities")]
    public Dictionary<string, object?> Capabilities { get; set; } = [];

    [JsonPropertyName("pricing")]
    public ModelPricingPlanRequest? Pricing { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed class ModelInfoUpdateRequest : ModelInfoCreateRequest
{
}

public sealed class ChannelModelInfoUpsertRequest
{
    [JsonPropertyName("upstream_model")]
    public string UpstreamModel { get; set; } = string.Empty;

    [JsonPropertyName("provider_code")]
    public string ProviderCode { get; set; } = string.Empty;

    [JsonPropertyName("provider_id")]
    public Guid? ProviderId { get; set; }

    [JsonPropertyName("model_key")]
    public string ModelKey { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("match_type")]
    public string MatchType { get; set; } = string.Empty;

    [JsonPropertyName("match_pattern")]
    public string MatchPattern { get; set; } = string.Empty;

    [JsonPropertyName("catalog")]
    public Dictionary<string, object?> Catalog { get; set; } = [];

    [JsonPropertyName("capabilities")]
    public Dictionary<string, object?> Capabilities { get; set; } = [];

    [JsonPropertyName("pricing")]
    public ModelPricingPlanRequest? Pricing { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed class ModelProviderUpsertRequest
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("sort_order")]
    public int SortOrder { get; set; }
}

public sealed class ModelBatchActionRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("ids")]
    public List<Guid> Ids { get; set; } = [];
}

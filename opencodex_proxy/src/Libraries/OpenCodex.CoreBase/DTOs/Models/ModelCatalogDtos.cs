using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.Models;

public sealed class ModelProviderResponse
{
    public ModelProviderResponse(
        Guid id,
        string code,
        string name,
        bool enabled,
        int sortOrder,
        string source,
        double createdAt,
        double updatedAt)
    {
        Id = id;
        Code = code;
        Name = name;
        Enabled = enabled;
        SortOrder = sortOrder;
        Source = source;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    [JsonPropertyName("id")]
    public Guid Id { get; }

    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    [JsonPropertyName("sort_order")]
    public int SortOrder { get; }

    [JsonPropertyName("source")]
    public string Source { get; }

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; }

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; }
}

public sealed class ModelPricingRuleResponse
{
    public ModelPricingRuleResponse(
        Guid id,
        string billingItem,
        string billingMode,
        decimal unitPrice,
        IReadOnlyList<object?> tiers,
        bool enabled)
    {
        Id = id;
        BillingItem = billingItem;
        BillingMode = billingMode;
        UnitPrice = unitPrice;
        Tiers = tiers;
        Enabled = enabled;
    }

    [JsonPropertyName("id")]
    public Guid Id { get; }

    [JsonPropertyName("billing_item")]
    public string BillingItem { get; }

    [JsonPropertyName("billing_mode")]
    public string BillingMode { get; }

    [JsonPropertyName("unit_price")]
    public decimal UnitPrice { get; }

    [JsonPropertyName("tiers")]
    public IReadOnlyList<object?> Tiers { get; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }
}

public sealed class ModelPricingPlanResponse
{
    public ModelPricingPlanResponse(
        Guid id,
        Guid? modelInfoId,
        Guid? channelModelInfoId,
        Guid? channelId,
        string currency,
        bool enabled,
        string source,
        IReadOnlyList<ModelPricingRuleResponse> rules,
        double createdAt,
        double updatedAt)
    {
        Id = id;
        ModelInfoId = modelInfoId;
        ChannelModelInfoId = channelModelInfoId;
        ChannelId = channelId;
        Currency = currency;
        Enabled = enabled;
        Source = source;
        Rules = rules;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    [JsonPropertyName("id")]
    public Guid Id { get; }

    [JsonPropertyName("model_info_id")]
    public Guid? ModelInfoId { get; }

    [JsonPropertyName("channel_model_info_id")]
    public Guid? ChannelModelInfoId { get; }

    [JsonPropertyName("channel_id")]
    public Guid? ChannelId { get; }

    [JsonPropertyName("currency")]
    public string Currency { get; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    [JsonPropertyName("source")]
    public string Source { get; }

    [JsonPropertyName("rules")]
    public IReadOnlyList<ModelPricingRuleResponse> Rules { get; }

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; }

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; }
}

public sealed class ModelInfoResponse
{
    public ModelInfoResponse(
        Guid id,
        string scope,
        Guid providerId,
        string providerCode,
        string providerName,
        Guid? channelId,
        string modelKey,
        string displayName,
        string description,
        string matchType,
        string matchPattern,
        IReadOnlyDictionary<string, object?> catalog,
        IReadOnlyDictionary<string, object?> capabilities,
        bool enabled,
        string source,
        ModelPricingPlanResponse? pricing,
        double createdAt,
        double updatedAt)
    {
        Id = id;
        Scope = scope;
        ProviderId = providerId;
        ProviderCode = providerCode;
        ProviderName = providerName;
        ChannelId = channelId;
        ModelKey = modelKey;
        DisplayName = displayName;
        Description = description;
        MatchType = matchType;
        MatchPattern = matchPattern;
        Catalog = catalog;
        Capabilities = capabilities;
        Enabled = enabled;
        Source = source;
        Pricing = pricing;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    [JsonPropertyName("id")]
    public Guid Id { get; }

    [JsonPropertyName("scope")]
    public string Scope { get; }

    [JsonPropertyName("provider_id")]
    public Guid ProviderId { get; }

    [JsonPropertyName("provider_code")]
    public string ProviderCode { get; }

    [JsonPropertyName("provider_name")]
    public string ProviderName { get; }

    [JsonPropertyName("channel_id")]
    public Guid? ChannelId { get; }

    [JsonPropertyName("model_key")]
    public string ModelKey { get; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; }

    [JsonPropertyName("description")]
    public string Description { get; }

    [JsonPropertyName("match_type")]
    public string MatchType { get; }

    [JsonPropertyName("match_pattern")]
    public string MatchPattern { get; }

    [JsonPropertyName("catalog")]
    public IReadOnlyDictionary<string, object?> Catalog { get; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyDictionary<string, object?> Capabilities { get; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    [JsonPropertyName("source")]
    public string Source { get; }

    [JsonPropertyName("pricing")]
    public ModelPricingPlanResponse? Pricing { get; }

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; }

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; }
}

public sealed class ModelProviderListResponse
{
    public ModelProviderListResponse(IReadOnlyList<ModelProviderResponse> providers)
    {
        Providers = providers;
    }

    [JsonPropertyName("providers")]
    public IReadOnlyList<ModelProviderResponse> Providers { get; }
}

public sealed class ModelInfoListResponse
{
    public ModelInfoListResponse(IReadOnlyList<ModelInfoResponse> models)
    {
        Models = models;
    }

    [JsonPropertyName("models")]
    public IReadOnlyList<ModelInfoResponse> Models { get; }
}

public sealed class ModelInfoResponsePayload
{
    public ModelInfoResponsePayload(ModelInfoResponse model)
    {
        Model = model;
    }

    [JsonPropertyName("model")]
    public ModelInfoResponse Model { get; }
}

public sealed class ChannelModelInfoResponse
{
    public ChannelModelInfoResponse(
        Guid id,
        Guid channelId,
        string upstreamModel,
        Guid providerId,
        string providerCode,
        string providerName,
        string modelKey,
        string displayName,
        string description,
        string matchType,
        string matchPattern,
        IReadOnlyDictionary<string, object?> catalog,
        IReadOnlyDictionary<string, object?> capabilities,
        bool enabled,
        string source,
        ModelPricingPlanResponse? pricing,
        double createdAt,
        double updatedAt)
    {
        Id = id;
        ChannelId = channelId;
        UpstreamModel = upstreamModel;
        ProviderId = providerId;
        ProviderCode = providerCode;
        ProviderName = providerName;
        ModelKey = modelKey;
        DisplayName = displayName;
        Description = description;
        MatchType = matchType;
        MatchPattern = matchPattern;
        Catalog = catalog;
        Capabilities = capabilities;
        Enabled = enabled;
        Source = source;
        Pricing = pricing;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    [JsonPropertyName("id")]
    public Guid Id { get; }

    [JsonPropertyName("channel_id")]
    public Guid ChannelId { get; }

    [JsonPropertyName("upstream_model")]
    public string UpstreamModel { get; }

    [JsonPropertyName("provider_id")]
    public Guid ProviderId { get; }

    [JsonPropertyName("provider_code")]
    public string ProviderCode { get; }

    [JsonPropertyName("provider_name")]
    public string ProviderName { get; }

    [JsonPropertyName("model_key")]
    public string ModelKey { get; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; }

    [JsonPropertyName("description")]
    public string Description { get; }

    [JsonPropertyName("match_type")]
    public string MatchType { get; }

    [JsonPropertyName("match_pattern")]
    public string MatchPattern { get; }

    [JsonPropertyName("catalog")]
    public IReadOnlyDictionary<string, object?> Catalog { get; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyDictionary<string, object?> Capabilities { get; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    [JsonPropertyName("source")]
    public string Source { get; }

    [JsonPropertyName("pricing")]
    public ModelPricingPlanResponse? Pricing { get; }

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; }

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; }
}

public sealed class ChannelModelInfoListItemResponse
{
    public ChannelModelInfoListItemResponse(
        string upstreamModel,
        bool overridden,
        ModelInfoResponse? globalModel,
        ChannelModelInfoResponse? overrideModel)
    {
        UpstreamModel = upstreamModel;
        Overridden = overridden;
        GlobalModel = globalModel;
        OverrideModel = overrideModel;
    }

    [JsonPropertyName("upstream_model")]
    public string UpstreamModel { get; }

    [JsonPropertyName("overridden")]
    public bool Overridden { get; }

    [JsonPropertyName("global_model")]
    public ModelInfoResponse? GlobalModel { get; }

    [JsonPropertyName("override_model")]
    public ChannelModelInfoResponse? OverrideModel { get; }
}

public sealed class ChannelModelInfoListResponse
{
    public ChannelModelInfoListResponse(
        Guid channelId,
        string channelName,
        IReadOnlyList<ChannelModelInfoListItemResponse> models)
    {
        ChannelId = channelId;
        ChannelName = channelName;
        Models = models;
    }

    [JsonPropertyName("channel_id")]
    public Guid ChannelId { get; }

    [JsonPropertyName("channel_name")]
    public string ChannelName { get; }

    [JsonPropertyName("models")]
    public IReadOnlyList<ChannelModelInfoListItemResponse> Models { get; }
}

public sealed class ChannelModelInfoResponsePayload
{
    public ChannelModelInfoResponsePayload(ChannelModelInfoResponse model)
    {
        Model = model;
    }

    [JsonPropertyName("model")]
    public ChannelModelInfoResponse Model { get; }
}

public sealed class ModelProviderResponsePayload
{
    public ModelProviderResponsePayload(ModelProviderResponse provider)
    {
        Provider = provider;
    }

    [JsonPropertyName("provider")]
    public ModelProviderResponse Provider { get; }
}

public sealed class SeedModelCatalogResponse
{
    public SeedModelCatalogResponse(int providersInserted, int modelsInserted, int modelsUpdated, int modelsSkipped)
    {
        ProvidersInserted = providersInserted;
        ModelsInserted = modelsInserted;
        ModelsUpdated = modelsUpdated;
        ModelsSkipped = modelsSkipped;
    }

    [JsonPropertyName("providers_inserted")]
    public int ProvidersInserted { get; }

    [JsonPropertyName("models_inserted")]
    public int ModelsInserted { get; }

    [JsonPropertyName("models_updated")]
    public int ModelsUpdated { get; }

    [JsonPropertyName("models_skipped")]
    public int ModelsSkipped { get; }
}

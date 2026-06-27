using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.Domain.Models;

public sealed class ModelUsageVector
{
    public ModelUsageVector(
        int inputTokens,
        int outputTokens,
        int cacheWriteTokens,
        int cacheReadTokens,
        int requestCount = 1)
    {
        InputTokens = Math.Max(0, inputTokens);
        OutputTokens = Math.Max(0, outputTokens);
        CacheWriteTokens = Math.Max(0, cacheWriteTokens);
        CacheReadTokens = Math.Max(0, cacheReadTokens);
        RequestCount = Math.Max(0, requestCount);
    }

    public int InputTokens { get; }

    public int OutputTokens { get; }

    public int CacheWriteTokens { get; }

    public int CacheReadTokens { get; }

    public int RequestCount { get; }
}

public sealed class ModelPricingCalculationResult
{
    public ModelPricingCalculationResult(
        decimal cost,
        string currency,
        Guid? modelInfoId,
        Guid? pricingPlanId,
        string? providerCode,
        string? modelKey,
        string? matchType,
        string? matchPattern,
        string resolution,
        string snapshotJson)
    {
        Cost = cost;
        Currency = currency;
        ModelInfoId = modelInfoId;
        PricingPlanId = pricingPlanId;
        ProviderCode = providerCode;
        ModelKey = modelKey;
        MatchType = matchType;
        MatchPattern = matchPattern;
        Resolution = resolution;
        SnapshotJson = snapshotJson;
    }

    public decimal Cost { get; }

    public string Currency { get; }

    public Guid? ModelInfoId { get; }

    public Guid? PricingPlanId { get; }

    public string? ProviderCode { get; }

    public string? ModelKey { get; }

    public string? MatchType { get; }

    public string? MatchPattern { get; }

    public string Resolution { get; }

    public string SnapshotJson { get; }
}

public sealed class ModelPricingSnapshot
{
    public ModelPricingSnapshot(
        string resolution,
        string currency,
        decimal cost,
        Guid? modelInfoId,
        Guid? pricingPlanId,
        string? providerCode,
        string? modelKey,
        string? matchType,
        string? matchPattern,
        IReadOnlyList<ModelPricingSnapshotRule> rules)
    {
        Resolution = resolution;
        Currency = currency;
        Cost = cost;
        ModelInfoId = modelInfoId;
        PricingPlanId = pricingPlanId;
        ProviderCode = providerCode;
        ModelKey = modelKey;
        MatchType = matchType;
        MatchPattern = matchPattern;
        Rules = rules;
    }

    [JsonPropertyName("resolution")]
    public string Resolution { get; }

    [JsonPropertyName("currency")]
    public string Currency { get; }

    [JsonPropertyName("cost")]
    public decimal Cost { get; }

    [JsonPropertyName("model_info_id")]
    public Guid? ModelInfoId { get; }

    [JsonPropertyName("pricing_plan_id")]
    public Guid? PricingPlanId { get; }

    [JsonPropertyName("provider_code")]
    public string? ProviderCode { get; }

    [JsonPropertyName("model_key")]
    public string? ModelKey { get; }

    [JsonPropertyName("match_type")]
    public string? MatchType { get; }

    [JsonPropertyName("match_pattern")]
    public string? MatchPattern { get; }

    [JsonPropertyName("rules")]
    public IReadOnlyList<ModelPricingSnapshotRule> Rules { get; }
}

public sealed class ModelPricingSnapshotRule
{
    public ModelPricingSnapshotRule(
        string billingItem,
        string billingMode,
        int quantity,
        decimal unitPrice,
        decimal cost)
    {
        BillingItem = billingItem;
        BillingMode = billingMode;
        Quantity = quantity;
        UnitPrice = unitPrice;
        Cost = cost;
    }

    [JsonPropertyName("billing_item")]
    public string BillingItem { get; }

    [JsonPropertyName("billing_mode")]
    public string BillingMode { get; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; }

    [JsonPropertyName("unit_price")]
    public decimal UnitPrice { get; }

    [JsonPropertyName("cost")]
    public decimal Cost { get; }
}

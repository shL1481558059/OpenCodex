using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.Models;

public sealed class ModelCatalogProviderTransfer
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

public sealed class ModelCatalogPricingTierTransfer
{
    [JsonPropertyName("up_to")]
    public long? UpTo { get; set; }

    [JsonPropertyName("unit_price")]
    public decimal UnitPrice { get; set; }
}

public sealed class ModelCatalogPricingRuleTransfer
{
    [JsonPropertyName("billing_item")]
    public string BillingItem { get; set; } = string.Empty;

    [JsonPropertyName("billing_mode")]
    public string BillingMode { get; set; } = string.Empty;

    [JsonPropertyName("unit_price")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("tiers")]
    public List<ModelCatalogPricingTierTransfer> Tiers { get; set; } = [];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed class ModelCatalogPricingTransfer
{
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("rules")]
    public List<ModelCatalogPricingRuleTransfer> Rules { get; set; } = [];
}

public sealed class ModelCatalogModelTransfer
{
    [JsonPropertyName("provider_code")]
    public string ProviderCode { get; set; } = string.Empty;

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

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("pricing")]
    public ModelCatalogPricingTransfer? Pricing { get; set; }
}

public sealed class ModelCatalogTransferDocument
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("exported_at")]
    public string ExportedAt { get; set; } = string.Empty;

    [JsonPropertyName("providers")]
    public List<ModelCatalogProviderTransfer> Providers { get; set; } = [];

    [JsonPropertyName("models")]
    public List<ModelCatalogModelTransfer> Models { get; set; } = [];
}

public sealed class ModelCatalogImportCounts
{
    [JsonPropertyName("created")]
    public int Created { get; init; }

    [JsonPropertyName("updated")]
    public int Updated { get; init; }

    [JsonPropertyName("unchanged")]
    public int Unchanged { get; init; }
}

public sealed class ModelCatalogImportResult
{
    [JsonPropertyName("dry_run")]
    public bool DryRun { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("providers")]
    public ModelCatalogImportCounts Providers { get; init; } = new();

    [JsonPropertyName("models")]
    public ModelCatalogImportCounts Models { get; init; } = new();

    [JsonPropertyName("skipped")]
    public int Skipped { get; init; }

    [JsonPropertyName("created_model_keys")]
    public IReadOnlyList<string> CreatedModelKeys { get; init; } = [];

    [JsonPropertyName("skipped_model_keys")]
    public IReadOnlyList<string> SkippedModelKeys { get; init; } = [];

    [JsonPropertyName("overwritten_model_keys")]
    public IReadOnlyList<string> OverwrittenModelKeys { get; init; } = [];

    [JsonPropertyName("pricing_deleted")]
    public int PricingDeleted { get; init; }

    [JsonPropertyName("error_count")]
    public int ErrorCount { get; init; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Controls how <see cref="IModelCatalogService.ImportModelCatalog"/> handles existing records.
/// </summary>
public sealed class ModelCatalogImportOptions
{
    /// <summary>When true, models whose model_key already exists locally are skipped (not modified).</summary>
    public bool SkipExistingModels { get; init; }

    /// <summary>When true, providers whose code already exists locally are skipped (name/sort/enabled not modified).</summary>
    public bool SkipExistingProviders { get; init; }

    /// <summary>When true, the local Enabled flag on existing models is never overwritten by the remote document.</summary>
    public bool PreserveLocalEnabled { get; init; }

    /// <summary>When true, a remote pricing: null does not delete the local pricing plan; the local plan is kept as-is.</summary>
    public bool KeepLocalPricingWhenRemoteNull { get; init; }

    /// <summary>The source tag to write on created/updated records (e.g. "manual", "sync").</summary>
    public string Source { get; init; } = "manual";
}

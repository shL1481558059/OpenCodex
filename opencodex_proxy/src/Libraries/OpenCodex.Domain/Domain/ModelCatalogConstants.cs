namespace OpenCodex.Core.Domain;

public static class ModelCatalogSources
{
    public const string SystemDefault = "system-default";

    public const string Manual = "manual";

    public const string MigratedModelPricing = "migrated-model-pricing";
}

public static class ModelInfoScopes
{
    public const string Global = "global";

    public const string Channel = "channel";
}

public static class ModelMatchTypes
{
    public const string Exact = "exact";

    public const string Prefix = "prefix";

    public const string Suffix = "suffix";

    public const string Contains = "contains";
}

public static class ModelBillingItems
{
    public const string Input = "input";

    public const string Output = "output";

    public const string CacheWrite = "cache_write";

    public const string CacheRead = "cache_read";
}

public static class ModelBillingModes
{
    public const string PerRequest = "per_request";

    public const string PerMillionTokens = "per_million_tokens";

    public const string TieredTokens = "tiered_tokens";
}

public static class ChannelModelPricingModes
{
    public const string InheritGlobal = "inherit_global";

    public const string OverridePricing = "override_pricing";

    public const string PrivateModel = "private_model";
}

namespace OpenCodex.Core.Domain;

public static class ModelCatalogSources
{
    public const string Manual = "manual";
}

public static class ModelInfoScopes
{
    public const string Global = "global";
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

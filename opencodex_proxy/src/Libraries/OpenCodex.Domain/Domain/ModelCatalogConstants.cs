namespace OpenCodex.Core.Domain;

public static class ModelCatalogSources
{
    public const string Manual = "manual";

    public const string Sync = "sync";
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

/// <summary>一次请求命中的计费时段。</summary>
public static class PricingPhases
{
    public const string Peak = "peak";

    public const string OffPeak = "off_peak";
}

/// <summary>时段判定的来源，用于解释账单。</summary>
public static class PricingPhaseSources
{
    /// <summary>价格计划未启用峰谷。</summary>
    public const string Disabled = "disabled";

    /// <summary>命中谷段窗口。</summary>
    public const string WindowHit = "window_hit";

    /// <summary>启用了峰谷但未命中任何谷段窗口。</summary>
    public const string WindowMiss = "window_miss";

    /// <summary>时区 ID 无法在当前运行环境解析，已按峰价计费。</summary>
    public const string TimeZoneUnresolved = "time_zone_unresolved";
}

namespace OpenCodex.CoreBase.DTOs;

public sealed class TavilyKeyDto(
    long id,
    int position,
    string provider,
    string key,
    bool enabled,
    int usageCount,
    int usageLimit,
    int keyUsageLimit)
{
    public long Id { get; } = id;

    public int Position { get; } = position;

    public string Provider { get; } = provider;

    public string Key { get; } = key;

    public bool Enabled { get; } = enabled;

    public int UsageCount { get; } = usageCount;

    public int UsageLimit { get; } = usageLimit;

    public int KeyUsageLimit { get; } = keyUsageLimit;
}

public sealed class WebSearchConfigDto(
    bool enabled,
    IReadOnlyList<string> providers,
    int defaultKeyUsageLimit,
    IReadOnlyList<TavilyKeyDto> keys)
{
    public bool Enabled { get; } = enabled;

    public IReadOnlyList<string> Providers { get; } = providers;

    public int DefaultKeyUsageLimit { get; } = defaultKeyUsageLimit;

    public IReadOnlyList<TavilyKeyDto> Keys { get; } = keys;
}

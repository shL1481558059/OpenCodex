namespace OpenCodex.CoreBase.DTOs;

/// <summary>
/// 表示已配置的 Tavily 兼容联网搜索访问密钥。
/// </summary>
/// <param name="id">密钥的数据库标识符。</param>
/// <param name="position">密钥选择位置。</param>
/// <param name="provider">Web 搜索提供方名称。</param>
/// <param name="key">API 密钥值。</param>
/// <param name="enabled">指示密钥是否启用的值。</param>
/// <param name="usageCount">密钥的当前使用次数。</param>
/// <param name="usageLimit">密钥配置的使用上限。</param>
/// <param name="keyUsageLimit">生效的单密钥使用上限。</param>
public sealed class TavilyKeyDto(
    Guid id,
    int position,
    string provider,
    string key,
    bool enabled,
    int usageCount,
    int usageLimit,
    int keyUsageLimit)
{
    /// <summary>
    /// 获取密钥的数据库标识符。
    /// </summary>
    public Guid Id { get; } = id;

    /// <summary>
    /// 获取密钥选择位置。
    /// </summary>
    public int Position { get; } = position;

    /// <summary>
    /// 获取联网搜索提供方名称。
    /// </summary>
    public string Provider { get; } = provider;

    /// <summary>
    /// 获取访问密钥值。
    /// </summary>
    public string Key { get; } = key;

    /// <summary>
    /// 获取指示密钥是否启用的值。
    /// </summary>
    public bool Enabled { get; } = enabled;

    /// <summary>
    /// 获取密钥的当前使用次数。
    /// </summary>
    public int UsageCount { get; } = usageCount;

    /// <summary>
    /// 获取密钥配置的使用上限。
    /// </summary>
    public int UsageLimit { get; } = usageLimit;

    /// <summary>
    /// 获取生效的单密钥使用上限。
    /// </summary>
    public int KeyUsageLimit { get; } = keyUsageLimit;
}

/// <summary>
/// 表示管理接口返回的联网搜索配置。
/// </summary>
/// <param name="enabled">指示 Web 搜索是否启用的值。</param>
/// <param name="providers">可用的 Web 搜索提供方名称。</param>
/// <param name="defaultKeyUsageLimit">应用到密钥的默认使用上限。</param>
/// <param name="keys">已配置的提供方密钥。</param>
public sealed class WebSearchConfigDto(
    bool enabled,
    IReadOnlyList<string> providers,
    int defaultKeyUsageLimit,
    IReadOnlyList<TavilyKeyDto> keys)
{
    /// <summary>
    /// 获取指示联网搜索是否启用的值。
    /// </summary>
    public bool Enabled { get; } = enabled;

    /// <summary>
    /// 获取可用的联网搜索提供方名称。
    /// </summary>
    public IReadOnlyList<string> Providers { get; } = providers;

    /// <summary>
    /// 获取应用到密钥的默认使用上限。
    /// </summary>
    public int DefaultKeyUsageLimit { get; } = defaultKeyUsageLimit;

    /// <summary>
    /// 获取已配置的提供方密钥。
    /// </summary>
    public IReadOnlyList<TavilyKeyDto> Keys { get; } = keys;
}

using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.WebSearch;

/// <summary>
/// 表示后台保存联网搜索配置时提交的请求。
/// </summary>
public sealed class WebSearchConfigRequest
{
    /// <summary>
    /// 获取或设置联网搜索是否启用。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// 获取或设置搜索密钥的默认使用次数上限。
    /// </summary>
    [JsonPropertyName("key_usage_limit")]
    public int? KeyUsageLimit { get; set; }

    /// <summary>
    /// 获取或设置搜索密钥配置列表。
    /// </summary>
    [JsonPropertyName("keys")]
    public List<TavilyKeyRequest> Keys { get; set; } = [];

    /// <summary>
    /// 将请求内容转换为配置字典。
    /// </summary>
    /// <returns>可写入配置存储的字典。</returns>
    public Dictionary<string, object?> ToDictionary()
    {
        var config = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["enabled"] = Enabled,
            ["keys"] = (Keys ?? [])
                .Select(key => key is null ? null : (object?)key.ToDictionary())
                .ToList()
        };
        if (KeyUsageLimit.HasValue)
        {
            config["key_usage_limit"] = KeyUsageLimit.Value;
        }

        return config;
    }
}

/// <summary>
/// 表示单个联网搜索密钥的保存请求。
/// </summary>
public sealed class TavilyKeyRequest
{
    /// <summary>
    /// 获取或设置既有密钥的标识。
    /// </summary>
    [JsonPropertyName("id")]
    public Guid? Id { get; set; }

    /// <summary>
    /// 获取或设置密钥所属的搜索提供方。
    /// </summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置密钥原文。
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置密钥是否启用。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// 获取或设置密钥已使用次数。
    /// </summary>
    [JsonPropertyName("usage_count")]
    public int? UsageCount { get; set; }

    /// <summary>
    /// 获取或设置密钥使用次数上限。
    /// </summary>
    [JsonPropertyName("usage_limit")]
    public int? UsageLimit { get; set; }

    /// <summary>
    /// 获取或设置兼容字段中的密钥使用次数上限。
    /// </summary>
    [JsonPropertyName("key_usage_limit")]
    public int? KeyUsageLimit { get; set; }

    /// <summary>
    /// 将密钥请求转换为配置字典。
    /// </summary>
    /// <returns>可写入配置存储的密钥字典。</returns>
    public Dictionary<string, object?> ToDictionary()
    {
        var key = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["provider"] = Provider,
            ["key"] = Key
        };

        if (Id.HasValue && Id.Value != Guid.Empty)
        {
            key["id"] = Id.Value;
        }

        if (Enabled.HasValue)
        {
            key["enabled"] = Enabled.Value;
        }

        if (UsageCount.HasValue)
        {
            key["usage_count"] = UsageCount.Value;
        }

        if (UsageLimit.HasValue)
        {
            key["usage_limit"] = UsageLimit.Value;
        }

        if (KeyUsageLimit.HasValue)
        {
            key["key_usage_limit"] = KeyUsageLimit.Value;
        }

        return key;
    }
}

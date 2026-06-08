using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.AdminWebSearch;

public sealed class WebSearchConfigRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("key_usage_limit")]
    public int? KeyUsageLimit { get; set; }

    [JsonPropertyName("keys")]
    public List<TavilyKeyRequest> Keys { get; set; } = [];

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

public sealed class TavilyKeyRequest
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("usage_count")]
    public int? UsageCount { get; set; }

    [JsonPropertyName("usage_limit")]
    public int? UsageLimit { get; set; }

    [JsonPropertyName("key_usage_limit")]
    public int? KeyUsageLimit { get; set; }

    public Dictionary<string, object?> ToDictionary()
    {
        var key = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["provider"] = Provider,
            ["key"] = Key
        };

        if (Id.HasValue)
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

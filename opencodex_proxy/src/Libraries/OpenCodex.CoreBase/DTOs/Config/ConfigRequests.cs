using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.DTOs.Config;

/// <summary>
/// 表示保存通道配置的请求。
/// </summary>
public sealed class ConfigSaveRequest
{
    /// <summary>
    /// 获取或设置要保存的通道请求列表。
    /// </summary>
    [JsonPropertyName("channels")]
    public List<ChannelRequest> Channels { get; set; } = [];

    /// <summary>
    /// 将请求转换为可持久化的字典结构。
    /// </summary>
    /// <returns>包含通道配置的字典。</returns>
    public Dictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["channels"] = (Channels ?? [])
                .Select(channel => channel is null ? null : (object?)channel.ToDictionary())
                .ToList()
        };
    }
}

/// <summary>
/// 表示单个通道配置请求。
/// </summary>
public sealed class ChannelRequest
{
    /// <summary>
    /// 获取或设置拥有该通道的用户名。
    /// </summary>
    [JsonPropertyName("owner_username")]
    public string? OwnerUsername { get; set; }

    /// <summary>
    /// 获取或设置通道标识符。
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// 获取或设置通道显示名称。
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// 获取或设置上游提供方类型。
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// 获取或设置上游基础 URL。
    /// </summary>
    [JsonPropertyName("baseurl")]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// 获取或设置上游 API 密钥。
    /// </summary>
    [JsonPropertyName("apikey")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// 获取或设置上游请求使用的认证模式。
    /// </summary>
    [JsonPropertyName("auth_mode")]
    public string? AuthMode { get; set; }

    /// <summary>
    /// 获取或设置应用到上游请求的附加请求头。
    /// </summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, object?> Headers { get; set; } = [];

    /// <summary>
    /// 获取或设置上游请求超时时间，单位为秒。
    /// </summary>
    [JsonPropertyName("timeout_seconds")]
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// 获取或设置上游请求重试次数。
    /// </summary>
    [JsonPropertyName("retry_count")]
    public int? RetryCount { get; set; }

    /// <summary>
    /// 获取或设置渠道优先级；值越小优先级越高。
    /// </summary>
    [JsonPropertyName("priority")]
    public int? Priority { get; set; }

    /// <summary>
    /// 获取或设置渠道允许的主请求并发上限；为空表示不限。
    /// </summary>
    [JsonPropertyName("capacity")]
    public int? Capacity { get; set; }

    /// <summary>
    /// 获取或设置通道兼容性选项。
    /// </summary>
    [JsonPropertyName("compat")]
    public Dictionary<string, object?> Compat { get; set; } = [];

    /// <summary>
    /// 获取或设置通道模型映射列表。
    /// </summary>
    [JsonPropertyName("models")]
    public List<ModelMappingRequest> Models { get; set; } = [];

    /// <summary>
    /// 获取或设置指示通道是否启用的值。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// 将通道请求转换为可持久化的字典结构。
    /// </summary>
    /// <returns>通道配置字典。</returns>
    public Dictionary<string, object?> ToDictionary()
    {
        var channel = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["headers"] = JsonRequestValue.Object(Headers),
            ["compat"] = JsonRequestValue.Object(Compat),
            ["models"] = (Models ?? [])
                .Select(model => model is null ? null : (object?)model.ToDictionary())
                .ToList()
        };

        Add(channel, "id", Id);
        Add(channel, "name", Name);
        Add(channel, "type", Type);
        Add(channel, "baseurl", BaseUrl);
        Add(channel, "apikey", ApiKey);
        Add(channel, "auth_mode", AuthMode);

        if (!string.IsNullOrWhiteSpace(OwnerUsername))
        {
            channel["owner_username"] = OwnerUsername;
        }

        if (TimeoutSeconds.HasValue)
        {
            channel["timeout_seconds"] = TimeoutSeconds.Value;
        }

        if (RetryCount.HasValue)
        {
            channel["retry_count"] = RetryCount.Value;
        }

        if (Priority.HasValue)
        {
            channel["priority"] = Priority.Value;
        }

        if (Capacity.HasValue)
        {
            channel["capacity"] = Capacity.Value;
        }

        if (Enabled.HasValue)
        {
            channel["enabled"] = Enabled.Value;
        }

        return channel;
    }

    /// <summary>
    /// 在值不为 <see langword="null"/> 时向通道字典添加字符串值。
    /// </summary>
    /// <param name="channel">要写入的通道字典。</param>
    /// <param name="key">要写入的键。</param>
    /// <param name="value">要写入的值。</param>
    private static void Add(
        Dictionary<string, object?> channel,
        string key,
        string? value)
    {
        if (value is not null)
        {
            channel[key] = value;
        }
    }
}

/// <summary>
/// 表示请求中的模型映射。
/// </summary>
public sealed class ModelMappingRequest
{
    /// <summary>
    /// 获取或设置对外暴露的模型名称。
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置上游模型名称。
    /// </summary>
    [JsonPropertyName("upstream_model")]
    public string UpstreamModel { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置指示模型是否支持图片输入的值。
    /// </summary>
    [JsonPropertyName("supports_image")]
    public bool SupportsImage { get; set; }

    /// <summary>
    /// 将模型映射请求转换为可持久化的字典结构。
    /// </summary>
    /// <returns>模型映射字典。</returns>
    public Dictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = Model,
            ["upstream_model"] = UpstreamModel,
            ["supports_image"] = SupportsImage
        };
    }
}

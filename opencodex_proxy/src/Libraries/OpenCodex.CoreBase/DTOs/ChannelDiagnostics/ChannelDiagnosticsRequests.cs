using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Config;

namespace OpenCodex.CoreBase.DTOs.ChannelDiagnostics;

/// <summary>
/// 表示通道诊断请求。
/// </summary>
public sealed class ChannelDiagnosticsRequest
{
    /// <summary>
    /// 获取或设置嵌套的通道配置请求。
    /// </summary>
    [JsonPropertyName("channel")]
    public ChannelRequest? Channel { get; set; }

    /// <summary>
    /// 获取或设置诊断时发送给上游的原始载荷。
    /// </summary>
    [JsonPropertyName("payload")]
    public Dictionary<string, object?>? Payload { get; set; }

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
    /// 获取或设置诊断使用的模型名称。
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// 获取或设置诊断输入文本。
    /// </summary>
    [JsonPropertyName("input")]
    public string? Input { get; set; }

    /// <summary>
    /// 获取或设置诊断请求的最大输出令牌数。
    /// </summary>
    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// 将诊断请求转换为字典结构。
    /// </summary>
    /// <returns>可供诊断服务使用的请求字典。</returns>
    public Dictionary<string, object?> ToDictionary()
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (Channel is not null)
        {
            body["channel"] = Channel.ToDictionary();
        }

        foreach (var (key, value) in FlatChannel().ToDictionary())
        {
            body[key] = value;
        }

        if (Payload is not null)
        {
            body["payload"] = JsonRequestValue.Object(Payload);
        }

        if (Model is not null)
        {
            body["model"] = Model;
        }

        if (Input is not null)
        {
            body["input"] = Input;
        }

        if (MaxOutputTokens.HasValue)
        {
            body["max_output_tokens"] = MaxOutputTokens.Value;
        }

        return body;
    }

    /// <summary>
    /// 根据扁平字段创建通道配置请求。
    /// </summary>
    /// <returns>由扁平字段构成的通道配置请求。</returns>
    private ChannelRequest FlatChannel()
    {
        return new ChannelRequest
        {
            Id = Id,
            Name = Name,
            Type = Type,
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            AuthMode = AuthMode,
            Headers = Headers ?? [],
            TimeoutSeconds = TimeoutSeconds,
            RetryCount = RetryCount,
            Compat = Compat ?? [],
            Models = Models ?? [],
            Enabled = Enabled
        };
    }
}

using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.DTOs.Config;

/// <summary>
/// 表示通道配置响应。
/// </summary>
public sealed class ConfigResponse
{
    /// <summary>
    /// 初始化 <see cref="ConfigResponse"/> 类的新实例。
    /// </summary>
    /// <param name="channels">通道响应列表。</param>
    public ConfigResponse(IReadOnlyList<ChannelResponse> channels)
    {
        Channels = channels;
    }

    /// <summary>
    /// 获取通道响应列表。
    /// </summary>
    [JsonPropertyName("channels")]
    public IReadOnlyList<ChannelResponse> Channels { get; }

    /// <summary>
    /// 根据通道 DTO 列表创建配置响应。
    /// </summary>
    /// <param name="channels">通道 DTO 列表。</param>
    /// <param name="activeRequestsResolver">解析渠道当前并发数的回调。</param>
    /// <returns>配置响应。</returns>
    public static ConfigResponse From(
        IReadOnlyList<ChannelDto> channels,
        Func<ChannelDto, int>? activeRequestsResolver = null,
        Func<ChannelDto, string>? healthStatusResolver = null)
    {
        return new ConfigResponse(channels.Select(channel => ChannelResponse.From(
            channel,
            activeRequestsResolver?.Invoke(channel) ?? 0,
            healthStatusResolver?.Invoke(channel) ?? "healthy")).ToList());
    }
}

/// <summary>
/// 表示单个通道配置响应。
/// </summary>
public sealed class ChannelResponse
{
    /// <summary>
    /// 初始化 <see cref="ChannelResponse"/> 类的新实例。
    /// </summary>
    /// <param name="ownerUsername">拥有该通道的用户名。</param>
    /// <param name="id">通道标识符。</param>
    /// <param name="name">通道显示名称。</param>
    /// <param name="type">上游提供方类型。</param>
    /// <param name="baseUrl">上游基础 URL。</param>
    /// <param name="apiKey">上游 API 密钥。</param>
    /// <param name="authMode">上游请求使用的认证模式。</param>
    /// <param name="headers">应用到上游请求的附加请求头。</param>
    /// <param name="timeoutSeconds">上游请求超时时间，单位为秒。</param>
    /// <param name="retryCount">重试次数。</param>
    /// <param name="priority">渠道优先级；值越小优先级越高。</param>
    /// <param name="capacity">渠道允许的主请求并发上限；为空表示不限。</param>
    /// <param name="activeRequests">渠道当前主请求并发占用数。</param>
    /// <param name="compat">通道兼容性选项。</param>
    /// <param name="models">通道配置的模型。</param>
    /// <param name="enabled">指示通道是否启用的值。</param>
    /// <param name="healthStatus">渠道运行时健康状态。</param>
    public ChannelResponse(
        string ownerUsername,
        string id,
        string name,
        string type,
        string baseUrl,
        string apiKey,
        string authMode,
        IReadOnlyDictionary<string, object?> headers,
        int timeoutSeconds,
        int retryCount,
        int priority,
        int? capacity,
        int activeRequests,
        IReadOnlyDictionary<string, object?> compat,
        IReadOnlyList<object?> models,
        bool enabled,
        string healthStatus)
    {
        OwnerUsername = ownerUsername;
        Id = id;
        Name = name;
        Type = type;
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        AuthMode = authMode;
        Headers = headers;
        TimeoutSeconds = timeoutSeconds;
        RetryCount = retryCount;
        Priority = priority;
        Capacity = capacity;
        ActiveRequests = activeRequests;
        Compat = compat;
        Models = models;
        Enabled = enabled;
        HealthStatus = healthStatus;
    }

    /// <summary>
    /// 获取拥有该通道的用户名。
    /// </summary>
    [JsonPropertyName("owner_username")]
    public string OwnerUsername { get; }

    /// <summary>
    /// 获取通道标识符。
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; }

    /// <summary>
    /// 获取通道显示名称。
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; }

    /// <summary>
    /// 获取上游提供方类型。
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; }

    /// <summary>
    /// 获取上游基础 URL。
    /// </summary>
    [JsonPropertyName("baseurl")]
    public string BaseUrl { get; }

    /// <summary>
    /// 获取上游 API 密钥。
    /// </summary>
    [JsonPropertyName("apikey")]
    public string ApiKey { get; }

    /// <summary>
    /// 获取上游请求使用的认证模式。
    /// </summary>
    [JsonPropertyName("auth_mode")]
    public string AuthMode { get; }

    /// <summary>
    /// 获取应用到上游请求的附加请求头。
    /// </summary>
    [JsonPropertyName("headers")]
    public IReadOnlyDictionary<string, object?> Headers { get; }

    /// <summary>
    /// 获取上游请求超时时间，单位为秒。
    /// </summary>
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; }

    /// <summary>
    /// 获取上游请求重试次数。
    /// </summary>
    [JsonPropertyName("retry_count")]
    public int RetryCount { get; }

    /// <summary>
    /// 获取渠道优先级；值越小优先级越高。
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority { get; }

    /// <summary>
    /// 获取渠道允许的主请求并发上限；为空表示不限。
    /// </summary>
    [JsonPropertyName("capacity")]
    public int? Capacity { get; }

    /// <summary>
    /// 获取渠道当前主请求并发占用数。
    /// </summary>
    [JsonPropertyName("active_requests")]
    public int ActiveRequests { get; }

    /// <summary>
    /// 获取通道兼容性选项。
    /// </summary>
    [JsonPropertyName("compat")]
    public IReadOnlyDictionary<string, object?> Compat { get; }

    /// <summary>
    /// 获取通道配置的模型。
    /// </summary>
    [JsonPropertyName("models")]
    public IReadOnlyList<object?> Models { get; }

    /// <summary>
    /// 获取指示通道是否启用的值。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    /// <summary>
    /// 获取渠道运行时健康状态。
    /// </summary>
    [JsonPropertyName("health_status")]
    public string HealthStatus { get; }

    /// <summary>
    /// 根据通道 DTO 创建通道响应。
    /// </summary>
    /// <param name="channel">通道 DTO。</param>
    /// <param name="activeRequests">渠道当前主请求并发占用数。</param>
    /// <returns>通道响应。</returns>
    public static ChannelResponse From(ChannelDto channel, int activeRequests, string healthStatus)
    {
        return new ChannelResponse(
            channel.OwnerUsername,
            channel.Id,
            channel.Name,
            channel.Type,
            channel.BaseUrl,
            channel.ApiKey,
            channel.AuthMode,
            channel.Headers,
            channel.TimeoutSeconds,
            channel.RetryCount,
            channel.Priority,
            channel.Capacity,
            activeRequests,
            channel.Compat,
            channel.Models,
            channel.Enabled,
            healthStatus);
    }
}

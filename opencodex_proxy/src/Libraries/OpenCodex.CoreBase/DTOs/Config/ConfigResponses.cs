using System.Text.Json;
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
    /// <returns>配置响应。</returns>
    public static ConfigResponse From(IReadOnlyList<ChannelDto> channels)
    {
        return new ConfigResponse(channels.Select(ChannelResponse.From).ToList());
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
    /// <param name="compat">通道兼容性选项。</param>
    /// <param name="models">通道配置的模型。</param>
    /// <param name="enabled">指示通道是否启用的值。</param>
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
        IReadOnlyDictionary<string, object?> compat,
        IReadOnlyList<object?> models,
        bool enabled)
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
        Compat = compat;
        Models = models;
        Enabled = enabled;
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
    /// 根据通道 DTO 创建通道响应。
    /// </summary>
    /// <param name="channel">通道 DTO。</param>
    /// <returns>通道响应。</returns>
    public static ChannelResponse From(ChannelDto channel)
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
            channel.Compat,
            channel.Models,
            channel.Enabled);
    }
}

/// <summary>
/// 表示配置导入响应。
/// </summary>
public sealed class ConfigImportResponse
{
    /// <summary>
    /// 初始化 <see cref="ConfigImportResponse"/> 类的新实例。
    /// </summary>
    /// <param name="config">导入后的配置响应。</param>
    /// <param name="imported">已导入的通道数量。</param>
    /// <param name="skipped">已跳过的通道数量。</param>
    /// <param name="skippedIds">已跳过的通道标识符列表。</param>
    public ConfigImportResponse(
        ConfigResponse config,
        int imported,
        int skipped,
        IReadOnlyList<string> skippedIds)
    {
        Config = config;
        Imported = imported;
        Skipped = skipped;
        SkippedIds = skippedIds;
    }

    /// <summary>
    /// 获取导入后的配置响应。
    /// </summary>
    [JsonPropertyName("config")]
    public ConfigResponse Config { get; }

    /// <summary>
    /// 获取已导入的通道数量。
    /// </summary>
    [JsonPropertyName("imported")]
    public int Imported { get; }

    /// <summary>
    /// 获取已跳过的通道数量。
    /// </summary>
    [JsonPropertyName("skipped")]
    public int Skipped { get; }

    /// <summary>
    /// 获取已跳过的通道标识符列表。
    /// </summary>
    [JsonPropertyName("skipped_ids")]
    public IReadOnlyList<string> SkippedIds { get; }

    /// <summary>
    /// 根据导入结果创建配置导入响应。
    /// </summary>
    /// <param name="config">导入后的通道 DTO 列表。</param>
    /// <param name="imported">已导入的通道数量。</param>
    /// <param name="skipped">已跳过的通道数量。</param>
    /// <param name="skippedIds">已跳过的通道标识符列表。</param>
    /// <returns>配置导入响应。</returns>
    public static ConfigImportResponse From(
        IReadOnlyList<ChannelDto> config,
        int imported,
        int skipped,
        IReadOnlyList<string> skippedIds)
    {
        return new ConfigImportResponse(
            ConfigResponse.From(config),
            imported,
            skipped,
            skippedIds);
    }
}

/// <summary>
/// 表示配置导出响应。
/// </summary>
public sealed class ConfigExportResponse
{
    /// <summary>
    /// 初始化 <see cref="ConfigExportResponse"/> 类的新实例。
    /// </summary>
    /// <param name="payload">导出的配置内容。</param>
    /// <param name="contentType">导出内容类型。</param>
    /// <param name="fileName">建议下载文件名。</param>
    public ConfigExportResponse(string payload, string contentType, string fileName)
    {
        Payload = payload;
        ContentType = contentType;
        FileName = fileName;
    }

    /// <summary>
    /// 获取导出的配置内容。
    /// </summary>
    public string Payload { get; }

    /// <summary>
    /// 获取导出内容类型。
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// 获取建议下载文件名。
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// 导出配置时使用的 JSON 序列化选项。
    /// </summary>
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// 根据通道 DTO 列表创建配置导出响应。
    /// </summary>
    /// <param name="channels">要导出的通道 DTO 列表。</param>
    /// <returns>配置导出响应。</returns>
    public static ConfigExportResponse From(IReadOnlyList<ChannelDto> channels)
    {
        var payload = JsonSerializer.Serialize(
            ConfigResponse.From(channels),
            ExportJsonOptions) + "\n";
        return new ConfigExportResponse(
            payload,
            "application/json",
            "opencodex-channels-config.json");
    }
}

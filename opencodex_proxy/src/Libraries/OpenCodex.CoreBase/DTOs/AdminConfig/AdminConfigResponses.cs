using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.DTOs.AdminConfig;

public sealed class ConfigResponse
{
    public ConfigResponse(IReadOnlyList<ChannelResponse> channels)
    {
        Channels = channels;
    }

    [JsonPropertyName("channels")]
    public IReadOnlyList<ChannelResponse> Channels { get; }

    public static ConfigResponse From(IReadOnlyList<ChannelDto> channels)
    {
        return new ConfigResponse(channels.Select(ChannelResponse.From).ToList());
    }
}

public sealed class ChannelResponse
{
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

    [JsonPropertyName("owner_username")]
    public string OwnerUsername { get; }

    [JsonPropertyName("id")]
    public string Id { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("type")]
    public string Type { get; }

    [JsonPropertyName("baseurl")]
    public string BaseUrl { get; }

    [JsonPropertyName("apikey")]
    public string ApiKey { get; }

    [JsonPropertyName("auth_mode")]
    public string AuthMode { get; }

    [JsonPropertyName("headers")]
    public IReadOnlyDictionary<string, object?> Headers { get; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; }

    [JsonPropertyName("retry_count")]
    public int RetryCount { get; }

    [JsonPropertyName("compat")]
    public IReadOnlyDictionary<string, object?> Compat { get; }

    [JsonPropertyName("models")]
    public IReadOnlyList<object?> Models { get; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

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

public sealed class ConfigImportResponse
{
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

    [JsonPropertyName("config")]
    public ConfigResponse Config { get; }

    [JsonPropertyName("imported")]
    public int Imported { get; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; }

    [JsonPropertyName("skipped_ids")]
    public IReadOnlyList<string> SkippedIds { get; }

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

public sealed class ConfigExportResponse
{
    public ConfigExportResponse(string payload, string contentType, string fileName)
    {
        Payload = payload;
        ContentType = contentType;
        FileName = fileName;
    }

    public string Payload { get; }

    public string ContentType { get; }

    public string FileName { get; }

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true
    };

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

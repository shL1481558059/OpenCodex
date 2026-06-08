using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs.Admin;

namespace OpenCodex.CoreBase.DTOs.AdminConfig;

public sealed class ConfigSaveRequest
{
    [JsonPropertyName("channels")]
    public List<ChannelRequest> Channels { get; set; } = [];

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

public sealed class ChannelRequest
{
    [JsonPropertyName("owner_username")]
    public string? OwnerUsername { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("baseurl")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("apikey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("auth_mode")]
    public string? AuthMode { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, object?> Headers { get; set; } = [];

    [JsonPropertyName("timeout_seconds")]
    public int? TimeoutSeconds { get; set; }

    [JsonPropertyName("retry_count")]
    public int? RetryCount { get; set; }

    [JsonPropertyName("compat")]
    public Dictionary<string, object?> Compat { get; set; } = [];

    [JsonPropertyName("models")]
    public List<ModelMappingRequest> Models { get; set; } = [];

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

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

        if (Enabled.HasValue)
        {
            channel["enabled"] = Enabled.Value;
        }

        return channel;
    }

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

public sealed class ModelMappingRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("upstream_model")]
    public string UpstreamModel { get; set; } = string.Empty;

    public Dictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = Model,
            ["upstream_model"] = UpstreamModel
        };
    }
}

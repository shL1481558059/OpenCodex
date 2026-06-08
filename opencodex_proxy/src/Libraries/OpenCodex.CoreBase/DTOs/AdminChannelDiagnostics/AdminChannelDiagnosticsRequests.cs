using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs.Admin;
using OpenCodex.CoreBase.DTOs.AdminConfig;

namespace OpenCodex.CoreBase.DTOs.AdminChannelDiagnostics;

public sealed class ChannelDiagnosticsRequest
{
    [JsonPropertyName("channel")]
    public ChannelRequest? Channel { get; set; }

    [JsonPropertyName("payload")]
    public Dictionary<string, object?>? Payload { get; set; }

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

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("input")]
    public string? Input { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

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

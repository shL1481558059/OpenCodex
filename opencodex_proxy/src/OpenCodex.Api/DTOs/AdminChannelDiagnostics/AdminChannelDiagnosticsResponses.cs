using System.Text.Json.Serialization;

namespace OpenCodex.Api.DTOs.AdminChannelDiagnostics;

public sealed class DiscoverModelsResponse
{
    public DiscoverModelsResponse(
        IReadOnlyList<string> models,
        IReadOnlyDictionary<string, object?> raw,
        int durationMs)
    {
        Models = models;
        Raw = raw;
        DurationMs = durationMs;
    }

    [JsonPropertyName("models")]
    public IReadOnlyList<string> Models { get; }

    [JsonPropertyName("raw")]
    public IReadOnlyDictionary<string, object?> Raw { get; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; }

    public static DiscoverModelsResponse From(
        IReadOnlyList<string> models,
        IReadOnlyDictionary<string, object?> raw,
        int durationMs)
    {
        return new DiscoverModelsResponse(
            models,
            raw,
            durationMs);
    }
}

public sealed class DiscoverModelsErrorResponse
{
    public DiscoverModelsErrorResponse(string error, int statusCode, int durationMs, object? body)
    {
        Error = error;
        StatusCode = statusCode;
        DurationMs = durationMs;
        Body = body;
    }

    [JsonPropertyName("error")]
    public string Error { get; }

    [JsonPropertyName("status_code")]
    public int StatusCode { get; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; }

    [JsonPropertyName("body")]
    public object? Body { get; }
}

public sealed class TestChannelResponse
{
    public TestChannelResponse(
        bool ok,
        int durationMs,
        string model,
        string upstreamModel,
        IReadOnlyList<string> compat,
        IReadOnlyDictionary<string, object?> response)
    {
        Ok = ok;
        DurationMs = durationMs;
        Model = model;
        UpstreamModel = upstreamModel;
        Compat = compat;
        Response = response;
    }

    [JsonPropertyName("ok")]
    public bool Ok { get; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; }

    [JsonPropertyName("model")]
    public string Model { get; }

    [JsonPropertyName("upstream_model")]
    public string UpstreamModel { get; }

    [JsonPropertyName("compat")]
    public IReadOnlyList<string> Compat { get; }

    [JsonPropertyName("response")]
    public IReadOnlyDictionary<string, object?> Response { get; }

    public static TestChannelResponse From(
        string model,
        string upstreamModel,
        IReadOnlyList<string> compat,
        IReadOnlyDictionary<string, object?> response,
        int durationMs)
    {
        return new TestChannelResponse(
            true,
            durationMs,
            model,
            upstreamModel,
            compat,
            response);
    }
}

public sealed class TestChannelErrorResponse
{
    public TestChannelErrorResponse(bool ok, int statusCode, int durationMs, string error, object? body)
    {
        Ok = ok;
        StatusCode = statusCode;
        DurationMs = durationMs;
        Error = error;
        Body = body;
    }

    [JsonPropertyName("ok")]
    public bool Ok { get; }

    [JsonPropertyName("status_code")]
    public int StatusCode { get; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; }

    [JsonPropertyName("error")]
    public string Error { get; }

    [JsonPropertyName("body")]
    public object? Body { get; }
}

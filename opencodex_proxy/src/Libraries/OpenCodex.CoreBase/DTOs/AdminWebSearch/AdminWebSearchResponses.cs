using System.Text.Json.Serialization;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.DTOs.AdminWebSearch;

public sealed class WebSearchConfigResponse
{
    public WebSearchConfigResponse(
        bool enabled,
        IReadOnlyList<string> providers,
        int defaultKeyUsageLimit,
        IReadOnlyList<TavilyKeyResponse> keys)
    {
        Enabled = enabled;
        Providers = providers;
        DefaultKeyUsageLimit = defaultKeyUsageLimit;
        Keys = keys;
    }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    [JsonPropertyName("providers")]
    public IReadOnlyList<string> Providers { get; }

    [JsonPropertyName("default_key_usage_limit")]
    public int DefaultKeyUsageLimit { get; }

    [JsonPropertyName("keys")]
    public IReadOnlyList<TavilyKeyResponse> Keys { get; }

    public static WebSearchConfigResponse From(WebSearchConfigDto config)
    {
        return new WebSearchConfigResponse(
            config.Enabled,
            config.Providers,
            config.DefaultKeyUsageLimit,
            config.Keys.Select(TavilyKeyResponse.From).ToList());
    }
}

public sealed class TavilyKeyResponse
{
    public TavilyKeyResponse(
        long id,
        int position,
        string provider,
        string key,
        bool enabled,
        int usageCount,
        int usageLimit,
        int keyUsageLimit)
    {
        Id = id;
        Position = position;
        Provider = provider;
        Key = key;
        Enabled = enabled;
        UsageCount = usageCount;
        UsageLimit = usageLimit;
        KeyUsageLimit = keyUsageLimit;
    }

    [JsonPropertyName("id")]
    public long Id { get; }

    [JsonPropertyName("position")]
    public int Position { get; }

    [JsonPropertyName("provider")]
    public string Provider { get; }

    [JsonPropertyName("key")]
    public string Key { get; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    [JsonPropertyName("usage_count")]
    public int UsageCount { get; }

    [JsonPropertyName("usage_limit")]
    public int UsageLimit { get; }

    [JsonPropertyName("key_usage_limit")]
    public int KeyUsageLimit { get; }

    public static TavilyKeyResponse From(TavilyKeyDto key)
    {
        return new TavilyKeyResponse(
            key.Id,
            key.Position,
            key.Provider,
            key.Key,
            key.Enabled,
            key.UsageCount,
            key.UsageLimit,
            key.KeyUsageLimit);
    }
}

public sealed class WebSearchTestKeyResponse
{
    public WebSearchTestKeyResponse(
        long id,
        string provider,
        int usageCount,
        int usageLimit,
        int keyUsageLimit)
    {
        Id = id;
        Provider = provider;
        UsageCount = usageCount;
        UsageLimit = usageLimit;
        KeyUsageLimit = keyUsageLimit;
    }

    [JsonPropertyName("id")]
    public long Id { get; }

    [JsonPropertyName("provider")]
    public string Provider { get; }

    [JsonPropertyName("usage_count")]
    public int UsageCount { get; }

    [JsonPropertyName("usage_limit")]
    public int UsageLimit { get; }

    [JsonPropertyName("key_usage_limit")]
    public int KeyUsageLimit { get; }

    public static WebSearchTestKeyResponse From(TavilyKeyDto key)
    {
        return new WebSearchTestKeyResponse(
            key.Id,
            key.Provider,
            key.UsageCount,
            key.UsageLimit,
            key.KeyUsageLimit);
    }
}

public sealed class WebSearchProviderResultResponse
{
    public WebSearchProviderResultResponse(
        bool ok,
        int? statusCode,
        int durationMs,
        string? errorType,
        string? error,
        WebSearchSummaryResponse summary,
        object? raw)
    {
        Ok = ok;
        StatusCode = statusCode;
        DurationMs = durationMs;
        ErrorType = errorType;
        Error = error;
        Summary = summary;
        Raw = raw;
    }

    [JsonPropertyName("ok")]
    public bool Ok { get; }

    [JsonPropertyName("status_code")]
    public int? StatusCode { get; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; }

    [JsonPropertyName("error_type")]
    public string? ErrorType { get; }

    [JsonPropertyName("error")]
    public string? Error { get; }

    [JsonPropertyName("summary")]
    public WebSearchSummaryResponse Summary { get; }

    [JsonPropertyName("raw")]
    public object? Raw { get; }

    public static WebSearchProviderResultResponse From(WebSearchProviderResult result)
    {
        return new WebSearchProviderResultResponse(
            result.Ok,
            result.StatusCode,
            result.DurationMs,
            result.ErrorType,
            result.Error,
            WebSearchSummaryResponse.From(result.Summary),
            result.Raw);
    }
}

public sealed class WebSearchSummaryResponse
{
    public WebSearchSummaryResponse(
        string answer,
        IReadOnlyList<object?> results,
        string? error)
    {
        Answer = answer;
        Results = results;
        Error = error;
    }

    [JsonPropertyName("answer")]
    public string Answer { get; }

    [JsonPropertyName("results")]
    public IReadOnlyList<object?> Results { get; }

    [JsonPropertyName("error")]
    public string? Error { get; }

    public static WebSearchSummaryResponse From(WebSearchSummary summary)
    {
        return new WebSearchSummaryResponse(
            summary.Answer,
            summary.Results.Select(item => (object?)item).ToList(),
            summary.Error);
    }
}

public sealed class WebSearchTestKeyResponsePayload
{
    public WebSearchTestKeyResponsePayload(
        bool ok,
        long durationMs,
        WebSearchTestKeyResponse key,
        WebSearchProviderResultResponse result,
        WebSearchConfigResponse config)
    {
        Ok = ok;
        DurationMs = durationMs;
        Key = key;
        Result = result;
        Config = config;
    }

    [JsonPropertyName("ok")]
    public bool Ok { get; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; }

    [JsonPropertyName("key")]
    public WebSearchTestKeyResponse Key { get; }

    [JsonPropertyName("result")]
    public WebSearchProviderResultResponse Result { get; }

    [JsonPropertyName("config")]
    public WebSearchConfigResponse Config { get; }

    public static WebSearchTestKeyResponsePayload From(
        TavilyKeyDto key,
        WebSearchProviderResult result,
        WebSearchConfigDto config,
        long durationMs)
    {
        return new WebSearchTestKeyResponsePayload(
            result.Ok,
            durationMs,
            WebSearchTestKeyResponse.From(key),
            WebSearchProviderResultResponse.From(result),
            WebSearchConfigResponse.From(config));
    }
}

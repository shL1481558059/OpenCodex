using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Config;

namespace OpenCodex.Api.ExternalIntegrations;

public sealed partial class HttpUpstreamClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static HttpRequestMessage BuildRequest(
        IReadOnlyDictionary<string, object?> channel,
        IReadOnlyDictionary<string, object?> payload,
        string endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, JoinUrl(JsonDictionaryValue.String(channel, "baseurl"), endpoint));
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        foreach (var header in BuildHeaders(channel))
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)
                && !string.Equals(header.Key, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return request;
    }

    private static HttpRequestMessage BuildGetRequest(
        IReadOnlyDictionary<string, object?> channel,
        string endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, JoinUrl(JsonDictionaryValue.String(channel, "baseurl"), endpoint));
        foreach (var header in BuildHeaders(channel))
        {
            if (!string.Equals(header.Key, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return request;
    }

    private static Dictionary<string, string> BuildHeaders(IReadOnlyDictionary<string, object?> channel)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-type"] = "application/json",
            ["user-agent"] = "OpenCodex-Proxy/0.1"
        };

        if (ConfigValue.TryAsObject(JsonDictionaryValue.Get(channel, "headers"), out var customHeaders))
        {
            foreach (var (key, value) in customHeaders)
            {
                headers[key] = value?.ToString() ?? string.Empty;
            }
        }

        var authMode = JsonDictionaryValue.String(channel, "auth_mode");
        if (authMode.Length == 0)
        {
            authMode = "config";
        }

        var apiKey = JsonDictionaryValue.String(channel, "apikey");
        var authValue = authMode == "config" && apiKey.Length > 0 ? $"Bearer {apiKey}" : null;
        if (JsonDictionaryValue.String(channel, "type") == "messages")
        {
            if (!string.IsNullOrEmpty(authValue) && authValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                headers["x-api-key"] = authValue["Bearer ".Length..];
            }
            else if (apiKey.Length > 0)
            {
                headers["x-api-key"] = apiKey;
            }

            if (!headers.ContainsKey("anthropic-version"))
            {
                headers["anthropic-version"] = "2023-06-01";
            }
        }
        else if (!string.IsNullOrEmpty(authValue))
        {
            headers["authorization"] = authValue;
        }

        return headers;
    }

    private static string JoinUrl(string baseUrl, string endpoint)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.Ordinal)
            ? $"{trimmed}{endpoint}"
            : $"{trimmed}/v1{endpoint}";
    }

    private static int TimeoutValue(object? value, int defaultTimeout)
    {
        return value is int intValue && intValue > 0 ? intValue : defaultTimeout;
    }

    private static int RetryCountValue(object? value)
    {
        return value is int intValue && intValue >= 0 ? intValue : 3;
    }
}

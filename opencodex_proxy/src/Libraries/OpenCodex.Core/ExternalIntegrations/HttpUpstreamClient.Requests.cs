using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Core.Config;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.ExternalIntegrations;

public sealed partial class HttpUpstreamClient
{
    private const string CodexDesktopUserAgent =
        "Codex Desktop/0.138.0-alpha.7 (Mac OS 13.7.8; arm64) unknown (Codex Desktop; 26.608.12217)";

    private const string ClaudeCliUserAgent = "claude-cli/2.1.145 (external, claude-vscode)";

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

        if (string.Equals(JsonDictionaryValue.String(channel, "type"), "messages", StringComparison.Ordinal)
            && payload.TryGetValue("mcp_servers", out var mcpServers)
            && mcpServers is IEnumerable<object?>)
        {
            const string mcpBeta = "mcp-client-2025-11-20";
            var betaValues = request.Headers.TryGetValues("anthropic-beta", out var existingValues)
                ? existingValues
                    .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToList()
                : [];
            if (!betaValues.Contains(mcpBeta, StringComparer.Ordinal))
            {
                betaValues.Add(mcpBeta);
            }

            request.Headers.Remove("anthropic-beta");
            request.Headers.TryAddWithoutValidation(
                "anthropic-beta",
                string.Join(", ", betaValues.Distinct(StringComparer.Ordinal)));
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
            ["content-type"] = "application/json"
        };

        if (ConfigValue.TryAsObject(JsonDictionaryValue.Get(channel, "headers"), out var customHeaders))
        {
            foreach (var (key, value) in customHeaders)
            {
                headers[key] = value?.ToString() ?? string.Empty;
            }
        }

        var channelType = JsonDictionaryValue.String(channel, "type");
        if (!headers.ContainsKey("user-agent"))
        {
            headers["user-agent"] = UserAgentForChannelType(channelType);
        }

        var authMode = JsonDictionaryValue.String(channel, "auth_mode");
        if (authMode.Length == 0)
        {
            authMode = "config";
        }

        var apiKey = JsonDictionaryValue.String(channel, "apikey");
        var authValue = authMode == "config" && apiKey.Length > 0 ? $"Bearer {apiKey}" : null;
        if (channelType == "messages")
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

    private static string UserAgentForChannelType(string channelType)
    {
        return channelType switch
        {
            "messages" => ClaudeCliUserAgent,
            "responses" or "chat" => CodexDesktopUserAgent,
            _ => "OpenCodex-Proxy/0.1"
        };
    }

    private static string JoinUrl(string baseUrl, string endpoint)
    {
        if (baseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            return $"{baseUrl}{endpoint.TrimStart('/')}";
        }

        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.Ordinal)
            ? $"{trimmed}{endpoint}"
            : $"{trimmed}/v1{endpoint}";
    }

    private static int TimeoutValue(object? value, int defaultTimeout)
    {
        return value switch
        {
            null => defaultTimeout,
            int intValue => intValue > 0 ? intValue : defaultTimeout,
            long longValue => longValue > 0 && longValue <= int.MaxValue ? (int)longValue : defaultTimeout,
            double doubleValue => doubleValue > 0 && doubleValue <= int.MaxValue ? (int)doubleValue : defaultTimeout,
            string text when int.TryParse(text, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 => parsed,
            _ => defaultTimeout
        };
    }

    private static int RetryCountValue(object? value)
    {
        return value switch
        {
            int intValue when intValue >= 0 => intValue,
            long longValue when longValue >= 0 && longValue <= int.MaxValue => (int)longValue,
            _ => 3
        };
    }
}

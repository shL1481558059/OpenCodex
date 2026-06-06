using OpenCodex.Api.Abstractions;

namespace OpenCodex.Api.Infrastructure;

public static class ProxyRequestMetadataFactory
{
    public static ProxyRequestMetadata FromHttpRequest(HttpRequest request, string? clientIp)
    {
        return new ProxyRequestMetadata(
            request.Method,
            request.Path.ToString(),
            clientIp,
            RedactedHeaders(request.Headers));
    }

    private static Dictionary<string, string> RedactedHeaders(IHeaderDictionary requestHeaders)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in requestHeaders)
        {
            headers[header.Key] = string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
                ? Redact(header.Value.ToString())
                : header.Value.ToString();
        }

        return headers;
    }

    private static string Redact(string value)
    {
        if (value.Length <= 12)
        {
            return "...";
        }

        return $"{value[..8]}...{value[^4..]}";
    }
}

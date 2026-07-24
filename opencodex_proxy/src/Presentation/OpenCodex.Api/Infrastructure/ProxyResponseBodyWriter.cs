using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Api.Infrastructure;

public sealed class ProxyResponseBodyWriter(HttpResponse response) : IProxyResponseBodyWriter
{
    private static readonly HashSet<string> SafeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache-control", "content-disposition", "etag", "expires", "last-modified", "retry-after", "x-request-id"
    };

    public async Task WriteAsync(
        int statusCode,
        string? contentType,
        IReadOnlyDictionary<string, string> headers,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        response.StatusCode = statusCode;
        if (!string.IsNullOrWhiteSpace(contentType)) response.ContentType = contentType;
        foreach (var header in headers)
        {
            if (SafeHeaders.Contains(header.Key)) response.Headers[header.Key] = header.Value;
        }
        await content.CopyToAsync(response.Body, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

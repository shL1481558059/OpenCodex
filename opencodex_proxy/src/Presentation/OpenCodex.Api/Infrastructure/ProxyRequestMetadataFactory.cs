using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Api.Infrastructure;

public static class ProxyRequestMetadataFactory
{
    public static ProxyRequestMetadata FromHttpRequest(HttpRequest request, string? clientIp)
    {
        var headers = RequestHeaders(request.Headers);
        var raw = RequestBodyReader.ReadCapturedRawBody(request);
        return new ProxyRequestMetadata(
            request.Method,
            request.Path.ToString(),
            clientIp,
            headers,
            raw)
        {
            LogBodies = raw is { Length: > 0 } bytes
                ? new ProxyLogBodyHandles(ProxyLogBodyHandle.CaptureJson(headers), ProxyLogBodyHandle.FromOwnedUtf8(bytes))
                : null
        };
    }

    private static Dictionary<string, string> RequestHeaders(IHeaderDictionary requestHeaders)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in requestHeaders)
        {
            headers[header.Key] = header.Value.ToString();
        }

        return headers;
    }
}

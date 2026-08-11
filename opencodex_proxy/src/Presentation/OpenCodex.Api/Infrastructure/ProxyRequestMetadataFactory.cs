using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Api.Infrastructure;

public static class ProxyRequestMetadataFactory
{
    public static ProxyRequestMetadata FromHttpRequest(HttpRequest request, string? clientIp)
    {
        return new ProxyRequestMetadata(
            request.Method,
            request.Path.ToString(),
            clientIp,
            RequestHeaders(request.Headers),
            RequestBodyReader.ReadCapturedRawBody(request));
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

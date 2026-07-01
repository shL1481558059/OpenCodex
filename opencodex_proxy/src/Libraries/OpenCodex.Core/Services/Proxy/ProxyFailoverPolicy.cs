using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Services.Proxy;

internal static class ProxyFailoverPolicy
{
    public static bool CanFailover(Exception exception)
    {
        if (exception is UpstreamException upstreamException
            && upstreamException.StatusCode is ProxyHttpStatus.BadRequest
                or ProxyHttpStatus.Forbidden)
        {
            return true;
        }

        if (exception is not ProxyException proxyException)
        {
            return false;
        }

        return proxyException.StatusCode is ProxyHttpStatus.TooManyRequests
            or ProxyHttpStatus.BadGateway
            or ProxyHttpStatus.GatewayTimeout
            or ProxyHttpStatus.InternalServerError
            or ProxyHttpStatus.ServiceUnavailable;
    }
}

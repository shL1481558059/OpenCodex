namespace OpenCodex.Api.Services;

public interface IProxyNonStreamService
{
    Task<ProxyNonStreamResult> SendAsync(ProxyNonStreamContext context);
}

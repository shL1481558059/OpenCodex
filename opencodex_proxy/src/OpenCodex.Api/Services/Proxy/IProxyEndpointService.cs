namespace OpenCodex.Api.Services;

public interface IProxyEndpointService
{
    Task<ProxyEndpointResult> ProxyAsync(ProxyEndpointContext context);
}

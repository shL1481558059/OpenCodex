namespace OpenCodex.CoreBase.Services.Proxy;

public interface IProxyEndpointService
{
    Task<ProxyEndpointResult> ProxyAsync(ProxyEndpointContext context);
}

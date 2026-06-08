namespace OpenCodex.CoreBase.Services.Proxy;

public interface IProxyNonStreamService
{
    Task<ProxyNonStreamResult> SendAsync(ProxyNonStreamContext context);
}

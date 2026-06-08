namespace OpenCodex.CoreBase.Services.Proxy;

public interface IProxyStreamService
{
    Task StreamAsync(ProxyStreamContext context);
}

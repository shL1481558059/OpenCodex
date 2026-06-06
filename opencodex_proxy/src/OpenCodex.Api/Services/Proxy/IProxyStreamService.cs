namespace OpenCodex.Api.Services;

public interface IProxyStreamService
{
    Task StreamAsync(ProxyStreamContext context);
}

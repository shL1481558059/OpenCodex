using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

public interface IProxyImageFallbackService
{
    Task<ProxyImageFallbackResult> RewriteAsync(ProxyImageFallbackContext context);
}

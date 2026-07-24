using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

public interface IProxyImagesEndpointService
{
    Task<ProxyEndpointResult> GenerateAsync(ImageGenerationContext context);

    Task<ProxyEndpointResult> EditAsync(ImageEditContext context);
}

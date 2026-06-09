using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

public interface IProxyOcrService
{
    Task<ProxyOcrResult> RecognizeAsync(ProxyOcrContext context);
}

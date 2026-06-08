using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.CoreBase.Services.Proxy;

public interface IProxyLogService
{
    long WriteLog(ProxyLogContext context, ProxyRequestMetadata request);

    long WriteLog(ProxyRequestLogContext context);
}

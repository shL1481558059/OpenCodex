using OpenCodex.Api.Abstractions;

namespace OpenCodex.Api.Services;

public interface IProxyLogService
{
    long WriteLog(ProxyLogContext context, ProxyRequestMetadata request);

    long WriteLog(ProxyRequestLogContext context);
}

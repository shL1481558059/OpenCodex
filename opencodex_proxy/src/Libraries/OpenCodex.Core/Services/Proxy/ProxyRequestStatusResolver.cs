using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Core.Services.Proxy;

internal static class ProxyRequestStatusResolver
{
    public const string ClientCancelledError = "client cancelled the request";

    public static string Resolve(int? statusCode, string? error)
    {
        if (statusCode == ProxyHttpStatus.ClientClosedRequest)
        {
            return ProxyRequestLifecycleStatus.Cancelled;
        }

        var status = statusCode ?? 0;
        return status >= 400 || !string.IsNullOrWhiteSpace(error)
            ? ProxyRequestLifecycleStatus.Failed
            : ProxyRequestLifecycleStatus.Success;
    }
}

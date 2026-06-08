using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.Services.Proxy;

public interface IProxyRequestService
{
    ProxyRequestState StartRequest();

    AuthenticatedAccessApiKeyDto AuthenticateAccessKey(string? authorizationHeader);
}

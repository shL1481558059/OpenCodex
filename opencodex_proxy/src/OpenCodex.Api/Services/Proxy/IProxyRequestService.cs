using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Services;

public interface IProxyRequestService
{
    ProxyRequestState StartRequest();

    AuthenticatedAccessApiKeyRecord AuthenticateAccessKey(string? authorizationHeader);
}

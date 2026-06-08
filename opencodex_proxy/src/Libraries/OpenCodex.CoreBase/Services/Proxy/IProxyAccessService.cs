using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.Services.Proxy;

public interface IProxyAccessService
{
    AuthenticatedAccessApiKeyDto AuthenticateBearer(string? authorizationHeader);
}

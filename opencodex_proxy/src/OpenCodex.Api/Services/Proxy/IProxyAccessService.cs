using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Services;

public interface IProxyAccessService
{
    AuthenticatedAccessApiKeyRecord AuthenticateBearer(string? authorizationHeader);
}

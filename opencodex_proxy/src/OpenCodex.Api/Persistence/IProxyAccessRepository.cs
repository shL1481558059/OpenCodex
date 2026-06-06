using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public interface IProxyAccessRepository
{
    AuthenticatedAccessApiKeyRecord? AuthenticateAccessApiKey(string? rawKey);
}

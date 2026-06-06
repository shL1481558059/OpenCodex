using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public interface IAdminApiKeyRepository
{
    IReadOnlyList<AccessApiKeyRecord> ListAccessApiKeys(string? ownerUsername);

    AccessApiKeyRecord CreateAccessApiKey(string ownerUsername, string name);

    AccessApiKeyRecord SetAccessApiKeyEnabled(long keyId, bool enabled, string? ownerUsername);

    void DeleteAccessApiKey(long keyId, string? ownerUsername);
}

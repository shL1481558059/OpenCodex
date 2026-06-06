using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public sealed class AdminApiKeyRepository : IAdminApiKeyRepository
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IRepository<AccessApiKeyEntity> _accessApiKeys;

    public AdminApiKeyRepository(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IRepository<AccessApiKeyEntity> accessApiKeys)
    {
        _settingsProvider = settingsProvider;
        _accessApiKeys = accessApiKeys;
    }

    public IReadOnlyList<AccessApiKeyRecord> ListAccessApiKeys(string? ownerUsername)
    {
        if (string.IsNullOrWhiteSpace(ownerUsername))
        {
            return _accessApiKeys.ListAll()
                .Select(key => key.ToRecord(includePlaintext: true))
                .ToList();
        }

        return OpenCodexDatabase.ListAccessApiKeys(_settingsProvider.GetSettings().DbPath, ownerUsername);
    }

    public AccessApiKeyRecord CreateAccessApiKey(string ownerUsername, string name)
    {
        return OpenCodexDatabase.CreateAccessApiKey(
            _settingsProvider.GetSettings().DbPath,
            ownerUsername,
            name);
    }

    public AccessApiKeyRecord SetAccessApiKeyEnabled(long keyId, bool enabled, string? ownerUsername)
    {
        return OpenCodexDatabase.SetAccessApiKeyEnabled(
            _settingsProvider.GetSettings().DbPath,
            keyId,
            enabled,
            ownerUsername);
    }

    public void DeleteAccessApiKey(long keyId, string? ownerUsername)
    {
        OpenCodexDatabase.DeleteAccessApiKey(
            _settingsProvider.GetSettings().DbPath,
            keyId,
            ownerUsername);
    }
}

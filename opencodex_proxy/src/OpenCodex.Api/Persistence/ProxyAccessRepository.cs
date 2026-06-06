using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public sealed class ProxyAccessRepository : IProxyAccessRepository
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public ProxyAccessRepository(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public AuthenticatedAccessApiKeyRecord? AuthenticateAccessApiKey(string? rawKey)
    {
        return OpenCodexDatabase.AuthenticateAccessApiKey(
            _settingsProvider.GetSettings().DbPath,
            rawKey);
    }
}

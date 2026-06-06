using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public sealed class ProxyRouteRepository : IProxyRouteRepository
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public ProxyRouteRepository(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public IReadOnlyList<ChannelRecord> ReadChannels(string ownerUsername)
    {
        var settings = _settingsProvider.GetSettings();
        return OpenCodexDatabase.ReadChannels(
            settings.DbPath,
            ownerUsername,
            settings.AdminUsername);
    }
}

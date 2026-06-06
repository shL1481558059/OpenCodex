using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public sealed class ProxyWebSearchRepository : IProxyWebSearchRepository
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IRepository<WebSearchSettingsEntity> _settings;
    private readonly IRepository<TavilyKeyEntity> _keys;

    public ProxyWebSearchRepository(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IRepository<WebSearchSettingsEntity> settings,
        IRepository<TavilyKeyEntity> keys)
    {
        _settingsProvider = settingsProvider;
        _settings = settings;
        _keys = keys;
    }

    public WebSearchConfigRecord ReadWebSearchConfig()
    {
        var settings = _settings.GetById(1);
        return new WebSearchConfigRecord(
            settings?.Enabled ?? false,
            OpenCodexDatabase.GetSupportedWebSearchProviders(),
            OpenCodexDatabase.GetDefaultWebSearchKeyUsageLimit(),
            _keys.ListAll().Select(key => key.ToRecord()).ToList());
    }

    public TavilyKeyRecord? ReserveTavilyKey()
    {
        return OpenCodexDatabase.ReserveTavilyKey(_settingsProvider.GetSettings().DbPath);
    }
}

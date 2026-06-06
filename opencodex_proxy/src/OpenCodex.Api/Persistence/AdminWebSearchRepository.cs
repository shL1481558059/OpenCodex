using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public sealed class AdminWebSearchRepository : IAdminWebSearchRepository
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IRepository<WebSearchSettingsEntity> _settings;
    private readonly IRepository<TavilyKeyEntity> _keys;

    public AdminWebSearchRepository(
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

    public WebSearchConfigRecord ReplaceWebSearchConfig(
        IReadOnlyDictionary<string, object?> config)
    {
        return OpenCodexDatabase.ReplaceWebSearchConfig(
            _settingsProvider.GetSettings().DbPath,
            config);
    }

    public TavilyKeyRecord? ReserveTavilyKeyById(long keyId)
    {
        return OpenCodexDatabase.ReserveTavilyKeyById(
            _settingsProvider.GetSettings().DbPath,
            keyId);
    }
}

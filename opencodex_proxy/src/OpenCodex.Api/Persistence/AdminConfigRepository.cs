using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public sealed class AdminConfigRepository : IAdminConfigRepository
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IRepository<ChannelEntity> _channels;

    public AdminConfigRepository(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IRepository<ChannelEntity> channels)
    {
        _settingsProvider = settingsProvider;
        _channels = channels;
    }

    public IReadOnlyList<ChannelRecord> ReadChannels(string? ownerUsername)
    {
        if (string.IsNullOrWhiteSpace(ownerUsername))
        {
            return _channels.ListAll()
                .Select(channel => channel.ToRecord())
                .ToList();
        }

        var settings = _settingsProvider.GetSettings();
        return OpenCodexDatabase.ReadChannels(
            settings.DbPath,
            ownerUsername,
            settings.AdminUsername);
    }

    public void ReplaceChannels(
        IEnumerable<IReadOnlyDictionary<string, object?>> channels,
        string? ownerUsername)
    {
        var settings = _settingsProvider.GetSettings();
        OpenCodexDatabase.ReplaceChannels(
            settings.DbPath,
            channels,
            settings.DefaultTimeout,
            ownerUsername,
            settings.AdminUsername);
    }
}

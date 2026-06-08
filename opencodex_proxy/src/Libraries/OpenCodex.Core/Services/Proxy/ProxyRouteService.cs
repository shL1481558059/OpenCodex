using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Config;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Routing;
using OpenCodex.Core.Services.Ef;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Routing;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyRouteService : IProxyRouteService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public ProxyRouteService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public RouteResult ChooseRoute(string ownerUsername, string? model)
    {
        var normalizedOwnerUsername = string.IsNullOrWhiteSpace(ownerUsername)
            ? string.Empty
            : ownerUsername.Trim();
        var settings = _settingsProvider.GetSettings();
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var query = context.Channels.AsNoTracking();
        if (normalizedOwnerUsername.Length > 0)
        {
            query = query.Where(channel => channel.OwnerUsername == normalizedOwnerUsername);
        }

        var channelConfigs = query
            .OrderBy(channel => channel.OwnerUsername)
            .ThenBy(channel => channel.Position)
            .ThenBy(channel => channel.Id)
            .AsEnumerable()
            .Select(EfServiceSupport.ToChannelDto)
            .Select(ChannelToConfig)
            .ToList<object?>();
        var config = new Dictionary<string, object?>
        {
            ["channels"] = channelConfigs
        };
        var expanded = ConfigEnvironmentExpander.Expand(config);
        if (!ConfigValue.TryAsObject(expanded, out var expandedObject))
        {
            throw new BadRequestException("expanded config must be an object");
        }

        return ChannelRouter.ChooseChannel(expandedObject, model);
    }

    private static Dictionary<string, object?> ChannelToConfig(ChannelDto channel)
    {
        return new Dictionary<string, object?>
        {
            ["owner_username"] = channel.OwnerUsername,
            ["id"] = channel.Id,
            ["name"] = channel.Name,
            ["type"] = channel.Type,
            ["baseurl"] = channel.BaseUrl,
            ["apikey"] = channel.ApiKey,
            ["auth_mode"] = channel.AuthMode,
            ["headers"] = channel.Headers,
            ["timeout_seconds"] = channel.TimeoutSeconds,
            ["retry_count"] = channel.RetryCount,
            ["compat"] = channel.Compat,
            ["models"] = channel.Models,
            ["enabled"] = channel.Enabled
        };
    }
}

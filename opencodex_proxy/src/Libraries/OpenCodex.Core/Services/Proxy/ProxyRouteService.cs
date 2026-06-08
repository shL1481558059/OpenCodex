using Mapster;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Config;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyRouteService : IProxyRouteService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public ProxyRouteService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public ProxyRouteDto ChooseRoute(string ownerUsername, string? model)
    {
        var enabledChannels = ListEnabledChannelConfigs(ownerUsername);

        if (enabledChannels.Count == 0)
        {
            throw new RoutingException("no enabled channels configured");
        }

        var normalizedModel = (model ?? string.Empty).Trim();
        var hasModelMappings = false;
        foreach (var channel in enabledChannels)
        {
            if (!channel.TryGetValue("models", out var modelsValue)
                || !ConfigValue.TryAsList(modelsValue, out var models))
            {
                continue;
            }

            foreach (var mappingValue in models)
            {
                if (ConfigValue.TryAsObject(mappingValue, out _))
                {
                    hasModelMappings = true;
                    break;
                }
            }

            if (hasModelMappings)
            {
                break;
            }
        }

        if (hasModelMappings)
        {
            foreach (var channel in enabledChannels)
            {
                if (!channel.TryGetValue("models", out var modelsValue)
                    || !ConfigValue.TryAsList(modelsValue, out var models))
                {
                    continue;
                }

                foreach (var mappingValue in models)
                {
                    if (!ConfigValue.TryAsObject(mappingValue, out var mapping))
                    {
                        continue;
                    }

                    if (mapping.TryGetValue("model", out var value)
                        && ConfigValue.PythonString(value) == normalizedModel)
                    {
                        var upstreamModel = mapping.TryGetValue("upstream_model", out var upstreamValue)
                            ? ConfigValue.PythonString(upstreamValue)
                            : string.Empty;

                        if (upstreamModel.Length == 0)
                        {
                            upstreamModel = normalizedModel;
                        }

                        return new ProxyRouteDto(channel, normalizedModel, upstreamModel);
                    }
                }
            }

            throw new RoutingException($"no enabled channel configured for model: {normalizedModel}");
        }

        return new ProxyRouteDto(enabledChannels[0], normalizedModel, normalizedModel);
    }

    public IReadOnlyList<string> ListModels(string ownerUsername)
    {
        var models = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in ListEnabledChannelConfigs(ownerUsername))
        {
            if (!channel.TryGetValue("models", out var modelsValue)
                || !ConfigValue.TryAsList(modelsValue, out var mappings))
            {
                continue;
            }

            foreach (var mappingValue in mappings)
            {
                if (!ConfigValue.TryAsObject(mappingValue, out var mapping))
                {
                    continue;
                }

                var model = mapping.TryGetValue("model", out var value)
                    ? ConfigValue.PythonString(value).Trim()
                    : string.Empty;
                if (model.Length > 0 && seen.Add(model))
                {
                    models.Add(model);
                }
            }
        }

        return models;
    }

    private List<Dictionary<string, object?>> ListEnabledChannelConfigs(string ownerUsername)
    {
        var channelValues = ReadExpandedChannelValues(ownerUsername);
        var enabledChannels = new List<Dictionary<string, object?>>();
        foreach (var channelValue in channelValues)
        {
            if (!ConfigValue.TryAsObject(channelValue, out var channel))
            {
                continue;
            }

            if (channel.TryGetValue("enabled", out var enabled) && enabled is false)
            {
                continue;
            }

            enabledChannels.Add(channel);
        }

        return enabledChannels;
    }

    private List<object?> ReadExpandedChannelValues(string ownerUsername)
    {
        var normalizedOwnerUsername = string.IsNullOrWhiteSpace(ownerUsername)
            ? string.Empty
            : ownerUsername.Trim();
        var settings = _settingsProvider.GetSettings();
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
            .Select(channel => channel.Adapt<ChannelDto>())
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

        if (!expandedObject.TryGetValue("channels", out var channelsValue)
            || !ConfigValue.TryAsList(channelsValue, out var channelValues))
        {
            throw new RoutingException("no enabled channels configured");
        }

        return channelValues;
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

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

    public ProxyRouteDto ChooseRoute(
        string ownerUsername,
        string? model,
        bool requestContainsImages = false)
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
                        var matchedRoute = ToCandidate(channel, mapping, normalizedModel);
                        if (!requestContainsImages || matchedRoute.SupportsImage)
                        {
                            return matchedRoute.ToRoute(normalizedModel);
                        }

                        var fallbackRoute = FindImageRouteInChannel(channel)
                            ?? FindImageRoute(enabledChannels);
                        if (fallbackRoute is null)
                        {
                            throw new RoutingException(
                                $"model {normalizedModel} does not support image input and no enabled image-capable model is configured");
                        }

                        return fallbackRoute.ToRoute(normalizedModel);
                    }
                }
            }

            throw new RoutingException($"no enabled channel configured for model: {normalizedModel}");
        }

        return new ProxyRouteDto(enabledChannels[0], normalizedModel, normalizedModel);
    }

    public IReadOnlyList<string> ListModels(string ownerUsername)
    {
        return ListModelCapabilities(ownerUsername)
            .Select(model => model.Model)
            .ToList();
    }

    public IReadOnlyList<ProxyModelCapabilityDto> ListModelCapabilities(string ownerUsername)
    {
        var capabilities = new List<ProxyModelCapabilityDto>();
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
                    capabilities.Add(new ProxyModelCapabilityDto(
                        model,
                        MappingSupportsImage(mapping)));
                }
            }
        }

        return capabilities;
    }

    private static ModelRouteCandidate? FindImageRouteInChannel(
        Dictionary<string, object?> channel)
    {
        if (!channel.TryGetValue("models", out var modelsValue)
            || !ConfigValue.TryAsList(modelsValue, out var models))
        {
            return null;
        }

        foreach (var mappingValue in models)
        {
            if (!ConfigValue.TryAsObject(mappingValue, out var mapping)
                || !MappingSupportsImage(mapping))
            {
                continue;
            }

            return ToCandidate(channel, mapping, string.Empty);
        }

        return null;
    }

    private static ModelRouteCandidate? FindImageRoute(
        IReadOnlyList<Dictionary<string, object?>> channels)
    {
        foreach (var channel in channels)
        {
            var route = FindImageRouteInChannel(channel);
            if (route is not null)
            {
                return route;
            }
        }

        return null;
    }

    private static ModelRouteCandidate ToCandidate(
        Dictionary<string, object?> channel,
        IReadOnlyDictionary<string, object?> mapping,
        string fallbackModel)
    {
        var model = mapping.TryGetValue("model", out var modelValue)
            ? ConfigValue.PythonString(modelValue).Trim()
            : string.Empty;
        if (model.Length == 0)
        {
            model = fallbackModel;
        }

        var upstreamModel = mapping.TryGetValue("upstream_model", out var upstreamValue)
            ? ConfigValue.PythonString(upstreamValue).Trim()
            : string.Empty;
        if (upstreamModel.Length == 0)
        {
            upstreamModel = model;
        }

        return new ModelRouteCandidate(
            channel,
            upstreamModel,
            MappingSupportsImage(mapping));
    }

    private static bool MappingSupportsImage(IReadOnlyDictionary<string, object?> mapping)
    {
        return mapping.TryGetValue("supports_image", out var value) && value is true;
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

    private sealed class ModelRouteCandidate
    {
        public ModelRouteCandidate(
            Dictionary<string, object?> channel,
            string upstreamModel,
            bool supportsImage)
        {
            Channel = channel;
            UpstreamModel = upstreamModel;
            SupportsImage = supportsImage;
        }

        public Dictionary<string, object?> Channel { get; }

        public string UpstreamModel { get; }

        public bool SupportsImage { get; }

        public ProxyRouteDto ToRoute(string originalModel)
        {
            return new ProxyRouteDto(Channel, originalModel, UpstreamModel);
        }
    }
}

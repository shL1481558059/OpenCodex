using Mapster;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Config;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Persistence;
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
        return ListRouteCandidates(ownerUsername, model, requestContainsImages)[0];
    }

    public IReadOnlyList<ProxyRouteDto> ListRouteCandidates(
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
        if (HasAnyModelMappings(enabledChannels))
        {
            var candidates = ListMatchedRouteCandidates(enabledChannels, normalizedModel);
            if (candidates.Count == 0)
            {
                throw new RoutingException($"no enabled channel configured for model: {normalizedModel}");
            }

            return candidates
                .Select(candidate => candidate.ToRoute())
                .ToList();
        }

        return
        [
            new ProxyRouteDto(
                enabledChannels[0],
                normalizedModel,
                normalizedModel,
                supportsImage: false,
                matchedModelMapping: false)
        ];
    }

    public IReadOnlyList<string> ListModels(string ownerUsername)
    {
        return ListModelCapabilities(ownerUsername)
            .Select(model => model.Model)
            .ToList();
    }

    public ProxyRouteDto? ChooseOcrRoute(string ownerUsername, string? model)
    {
        var enabledChannels = ListEnabledChannelConfigs(ownerUsername);
        if (enabledChannels.Count == 0)
        {
            return null;
        }

        var normalizedModel = (model ?? string.Empty).Trim();
        if (normalizedModel.Length == 0)
        {
            return null;
        }

        var candidates = ListMatchedRouteCandidates(enabledChannels, normalizedModel);
        if (candidates.Count == 0)
        {
            return null;
        }

        var primaryChannel = candidates[0].Channel;
        var sameChannelRoute = FindImageRouteInChannel(primaryChannel);
        if (sameChannelRoute is not null)
        {
            return sameChannelRoute.ToRoute();
        }

        return FindImageRoute(enabledChannels, primaryChannel)
            ?.ToRoute();
    }

    public IReadOnlyList<ProxyModelCapabilityDto> ListModelCapabilities(string ownerUsername)
    {
        var bestCandidates = new Dictionary<string, ModelRouteCandidate>(StringComparer.Ordinal);
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

                var candidate = ToCandidate(channel, mapping, string.Empty);
                if (candidate.Model.Length == 0)
                {
                    continue;
                }

                if (!bestCandidates.TryGetValue(candidate.Model, out var current)
                    || candidate.CompareTo(current) < 0)
                {
                    bestCandidates[candidate.Model] = candidate;
                }
            }
        }

        return bestCandidates.Values
            .OrderBy(candidate => candidate)
            .ThenBy(candidate => candidate.Model, StringComparer.Ordinal)
            .Select(candidate => new ProxyModelCapabilityDto(
                candidate.Model,
                candidate.SupportsImage))
            .ToList();
    }

    private static bool HasAnyModelMappings(IReadOnlyList<Dictionary<string, object?>> channels)
    {
        foreach (var channel in channels)
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
                    return true;
                }
            }
        }

        return false;
    }

    private static List<ModelRouteCandidate> ListMatchedRouteCandidates(
        IReadOnlyList<Dictionary<string, object?>> channels,
        string normalizedModel)
    {
        var candidates = new List<ModelRouteCandidate>();
        foreach (var channel in channels)
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
                    && ConfigValue.PythonString(value).Trim() == normalizedModel)
                {
                    candidates.Add(ToCandidate(channel, mapping, normalizedModel));
                }
            }
        }

        candidates.Sort(static (left, right) => left.CompareTo(right));
        return candidates;
    }

    private static ModelRouteCandidate? FindImageRouteInChannel(
        Dictionary<string, object?> channel)
    {
        if (!channel.TryGetValue("models", out var modelsValue)
            || !ConfigValue.TryAsList(modelsValue, out var models))
        {
            return null;
        }

        ModelRouteCandidate? best = null;
        foreach (var mappingValue in models)
        {
            if (!ConfigValue.TryAsObject(mappingValue, out var mapping)
                || !MappingSupportsImage(mapping))
            {
                continue;
            }

            var candidate = ToCandidate(channel, mapping, string.Empty);
            if (best is null || candidate.CompareTo(best) < 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static ModelRouteCandidate? FindImageRoute(
        IReadOnlyList<Dictionary<string, object?>> channels,
        Dictionary<string, object?>? skipChannel = null)
    {
        ModelRouteCandidate? best = null;
        foreach (var channel in channels)
        {
            if (skipChannel is not null && ReferenceEquals(channel, skipChannel))
            {
                continue;
            }

            var route = FindImageRouteInChannel(channel);
            if (route is null)
            {
                continue;
            }

            if (best is null || route.CompareTo(best) < 0)
            {
                best = route;
            }
        }

        return best;
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
            model,
            upstreamModel,
            MappingSupportsImage(mapping),
            PriorityValue(channel),
            PositionValue(channel));
    }

    private static bool MappingSupportsImage(IReadOnlyDictionary<string, object?> mapping)
    {
        return mapping.TryGetValue("supports_image", out var value) && value is true;
    }

    private static int PriorityValue(IReadOnlyDictionary<string, object?> channel)
    {
        return channel.TryGetValue("priority", out var value) && value is int priority
            ? priority
            : 0;
    }

    private static int PositionValue(IReadOnlyDictionary<string, object?> channel)
    {
        return channel.TryGetValue("position", out var value) && value is int position
            ? position
            : 0;
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
        OpenCodexChannels.EnsureSchema(context);
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
            ["priority"] = channel.Priority,
            ["capacity"] = channel.Capacity,
            ["position"] = channel.Position,
            ["compat"] = channel.Compat,
            ["models"] = channel.Models,
            ["enabled"] = channel.Enabled
        };
    }

    private sealed class ModelRouteCandidate : IComparable<ModelRouteCandidate>
    {
        public ModelRouteCandidate(
            Dictionary<string, object?> channel,
            string model,
            string upstreamModel,
            bool supportsImage,
            int priority,
            int position)
        {
            Channel = channel;
            Model = model;
            UpstreamModel = upstreamModel;
            SupportsImage = supportsImage;
            Priority = priority;
            Position = position;
        }

        public Dictionary<string, object?> Channel { get; }

        public string Model { get; }

        public string UpstreamModel { get; }

        public bool SupportsImage { get; }

        public int Priority { get; }

        public int Position { get; }

        public int CompareTo(ModelRouteCandidate? other)
        {
            if (other is null)
            {
                return -1;
            }

            var priorityComparison = Priority.CompareTo(other.Priority);
            if (priorityComparison != 0)
            {
                return priorityComparison;
            }

            var positionComparison = Position.CompareTo(other.Position);
            if (positionComparison != 0)
            {
                return positionComparison;
            }

            return string.Compare(
                ConfigValue.PythonString(Channel["id"]),
                ConfigValue.PythonString(other.Channel["id"]),
                StringComparison.Ordinal);
        }

        public ProxyRouteDto ToRoute()
        {
            return new ProxyRouteDto(
                Channel,
                Model,
                UpstreamModel,
                SupportsImage,
                matchedModelMapping: true);
        }
    }
}

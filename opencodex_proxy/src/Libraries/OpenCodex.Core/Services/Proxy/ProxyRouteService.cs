using System.Text.Json;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Config;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyRouteService : IProxyRouteService
{
    private readonly IRepository<Channel> _channelRepository;
    private readonly IRepository<User> _userRepository;
    private readonly IModelCatalogService _catalog;

    public ProxyRouteService(
        IRepository<Channel> channelRepository,
        IRepository<User> userRepository,
        IModelCatalogService catalog)
    {
        _channelRepository = channelRepository;
        _userRepository = userRepository;
        _catalog = catalog;
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

    private List<ModelRouteCandidate> ListMatchedRouteCandidates(
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

    private ModelRouteCandidate? FindImageRouteInChannel(
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
                || !MappingSupportsImage(channel, mapping))
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

    private ModelRouteCandidate? FindImageRoute(
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

    private ModelRouteCandidate ToCandidate(
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
            MappingSupportsImage(channel, mapping, upstreamModel),
            PriorityValue(channel),
            PositionValue(channel));
    }

    private bool MappingSupportsImage(
        IReadOnlyDictionary<string, object?> channel,
        IReadOnlyDictionary<string, object?> mapping,
        string? upstreamModel = null)
    {
        var legacyMappingValue = mapping.TryGetValue("supports_image", out var value) && value is true;
        var actualUpstreamModel = string.IsNullOrWhiteSpace(upstreamModel)
            ? JsonDictionaryValue.String(mapping, "upstream_model")
            : upstreamModel;
        if (string.IsNullOrWhiteSpace(actualUpstreamModel))
        {
            actualUpstreamModel = JsonDictionaryValue.String(mapping, "model");
        }

        return _catalog.SupportsImage(ParseChannelId(channel), actualUpstreamModel, legacyMappingValue);
    }

    private static Guid? ParseChannelId(IReadOnlyDictionary<string, object?> channel)
    {
        if (!channel.TryGetValue("id", out var value))
        {
            return null;
        }

        if (value is Guid guidValue)
        {
            return guidValue;
        }

        return Guid.TryParse(value?.ToString(), out var parsed) ? parsed : null;
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

        var query = _channelRepository.TableNoTracking;
        if (normalizedOwnerUsername.Length > 0)
        {
            // 按 owner username 过滤:先查 User 拿 UserId
            var ownerUser = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == normalizedOwnerUsername);
            if (ownerUser is null)
            {
                return [];
            }
            query = query.Where(channel => channel.OwnerUserId == ownerUser.Id);
        }

        var channels = query
            .OrderBy(channel => channel.OwnerUserId)
            .ThenBy(channel => channel.Position)
            .ThenBy(channel => channel.Id)
            .ToList();

        // 手动 join User 拿 username(禁止导航属性)
        var ownerIds = channels.Select(ch => ch.OwnerUserId).Distinct().ToList();
        var owners = ownerIds.Count > 0
            ? _userRepository.TableNoTracking
                .Where(u => ownerIds.Contains(u.Id))
                .ToDictionary(u => u.Id, u => u.Username)
            : new Dictionary<Guid, string>();

        var channelConfigs = channels
            .Select(channel => ChannelToConfig(MapToChannelDto(channel,
                owners.TryGetValue(channel.OwnerUserId, out var name) ? name : string.Empty)))
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

    private static ChannelDto MapToChannelDto(Channel channel, string ownerUsername)
    {
        return new ChannelDto(
            channel.Id,
            channel.OwnerUserId,
            ownerUsername,
            channel.Position,
            channel.Name,
            channel.Type,
            channel.BaseUrl,
            channel.ApiKey,
            channel.AuthMode,
            DeserializeObject(channel.HeadersJson),
            channel.TimeoutSeconds,
            channel.RetryCount,
            channel.Priority,
            channel.Capacity,
            DeserializeObject(channel.CompatJson),
            DeserializeList(channel.ModelsJson),
            channel.Enabled);
    }

    private static IReadOnlyDictionary<string, object?> DeserializeObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return FromJsonElement(document.RootElement) as Dictionary<string, object?>
                ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static IReadOnlyList<object?> DeserializeList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return FromJsonElement(document.RootElement) as List<object?> ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => FromJsonElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };
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

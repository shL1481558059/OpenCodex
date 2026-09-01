using System.Text.Json;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Config;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Caching;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyRouteService : IProxyRouteService
{
    private static readonly TimeSpan RouteCacheTtl = TimeSpan.FromSeconds(60);

    private readonly IRepository<Channel> _channelRepository;
    private readonly IRepository<User> _userRepository;
    private readonly IModelCatalogService _catalog;
    private readonly ICacheService _cache;
    private readonly IVisionTransferSettingsService _visionTransferSettings;

    public ProxyRouteService(
        IRepository<Channel> channelRepository,
        IRepository<User> userRepository,
        IModelCatalogService catalog,
        ICacheService cache,
        IVisionTransferSettingsService visionTransferSettings)
    {
        _channelRepository = channelRepository;
        _userRepository = userRepository;
        _catalog = catalog;
        _cache = cache;
        _visionTransferSettings = visionTransferSettings;
    }

    public async Task<IReadOnlyList<ProxyRouteDto>> ListRouteCandidatesAsync(
        string ownerUsername,
        string? model)
    {
        return await ListRouteCandidatesAsync(ownerUsername, model, allowedChannelTypes: null);
    }

    public async Task<IReadOnlyList<ProxyRouteDto>> ListRouteCandidatesAsync(
        string ownerUsername,
        string? model,
        IReadOnlySet<string>? allowedChannelTypes)
    {
        var enabledChannels = await ListEnabledChannelConfigsAsync(ownerUsername);
        if (allowedChannelTypes is not null)
        {
            enabledChannels = enabledChannels
                .Where(channel => allowedChannelTypes.Contains(JsonDictionaryValue.String(channel, "type")))
                .ToList();
        }
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

    public async Task<VisionTransferRoutesDto> ListVisionTransferRoutesAsync(string ownerUsername)
    {
        var normalizedOwner = (ownerUsername ?? string.Empty).Trim();
        var ownerId = normalizedOwner.Length == 0
            ? (Guid?)null
            : _userRepository.TableNoTracking
                .Where(user => user.Username == normalizedOwner)
                .Select(user => (Guid?)user.Id)
                .FirstOrDefault();
        if (!ownerId.HasValue)
        {
            return VisionTransferRoutesDto.NotConfigured();
        }

        var snapshot = _visionTransferSettings.GetSnapshot(ownerId.Value);
        if (snapshot is null)
        {
            return VisionTransferRoutesDto.NotConfigured();
        }

        var enabledChannels = await ListEnabledChannelConfigsAsync(normalizedOwner);
        var candidates = new List<ProxyRouteDto>(2);
        var reason = string.Empty;

        var primary = ResolveConfiguredRoute(
            enabledChannels,
            snapshot.PrimaryChannelId,
            snapshot.PrimaryModel,
            out var primaryReason);
        if (primary is not null)
        {
            candidates.Add(primary);
        }
        else
        {
            reason = primaryReason;
        }

        if (snapshot.FallbackChannelId.HasValue && !string.IsNullOrWhiteSpace(snapshot.FallbackModel))
        {
            var fallback = ResolveConfiguredRoute(
                enabledChannels,
                snapshot.FallbackChannelId.Value,
                snapshot.FallbackModel!,
                out var fallbackReason);
            if (fallback is not null)
            {
                candidates.Add(fallback);
            }
            else if (candidates.Count == 0)
            {
                // 主与兜底都失效时,报告兜底的失效原因更贴近"最后一根稻草"。
                reason = fallbackReason;
            }
        }

        return new VisionTransferRoutesDto(
            configured: true,
            candidates,
            candidates.Count == 0 ? reason : string.Empty);
    }

    public async Task<IReadOnlyList<ProxyModelCapabilityDto>> ListModelCapabilitiesAsync(string ownerUsername)
    {
        var bestCandidates = new Dictionary<string, ModelRouteCandidate>(StringComparer.Ordinal);
        foreach (var channel in await ListEnabledChannelConfigsAsync(ownerUsername))
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
                candidate.SupportsImage,
                ParseChannelId(candidate.Channel),
                JsonDictionaryValue.String(candidate.Channel, "name"),
                candidate.UpstreamModel,
                candidate.Priority,
                candidate.Position))
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

    /// <summary>
    /// 按显式配置解析一条视觉转移路由。渠道集合已按 owner 过滤且只含启用渠道,
    /// 因此渠道被删、被禁用或换主都表现为在集合中找不到。
    /// </summary>
    private ProxyRouteDto? ResolveConfiguredRoute(
        IReadOnlyList<Dictionary<string, object?>> enabledChannels,
        Guid channelId,
        string model,
        out string reason)
    {
        var channel = enabledChannels.FirstOrDefault(item => ParseChannelId(item) == channelId);
        if (channel is null)
        {
            reason = VisionTransferUnavailableReasons.ChannelDeletedOrDisabled;
            return null;
        }

        var normalizedModel = (model ?? string.Empty).Trim();
        if (channel.TryGetValue("models", out var modelsValue)
            && ConfigValue.TryAsList(modelsValue, out var models))
        {
            foreach (var mappingValue in models)
            {
                if (!ConfigValue.TryAsObject(mappingValue, out var mapping)
                    || !mapping.TryGetValue("model", out var value)
                    || ConfigValue.PythonString(value).Trim() != normalizedModel)
                {
                    continue;
                }

                var candidate = ToCandidate(channel, mapping, normalizedModel);
                if (!candidate.SupportsImage)
                {
                    reason = VisionTransferUnavailableReasons.ImageCapabilityRevoked;
                    return null;
                }

                reason = string.Empty;
                return candidate.ToRoute();
            }
        }

        reason = VisionTransferUnavailableReasons.ModelMappingMissing;
        return null;
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
        var actualUpstreamModel = string.IsNullOrWhiteSpace(upstreamModel)
            ? JsonDictionaryValue.String(mapping, "upstream_model")
            : upstreamModel;
        if (string.IsNullOrWhiteSpace(actualUpstreamModel))
        {
            actualUpstreamModel = JsonDictionaryValue.String(mapping, "model");
        }

        return _catalog.SupportsImage(ParseChannelId(channel), actualUpstreamModel);
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

    private async Task<List<Dictionary<string, object?>>> ListEnabledChannelConfigsAsync(string ownerUsername)
    {
        var channelValues = await ReadExpandedChannelValuesAsync(ownerUsername);
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

    private async Task<List<object?>> ReadExpandedChannelValuesAsync(string ownerUsername)
    {
        // 缓存原始渠道实体集(强类型,JSON 干净往返),避免松类型展开结果往返成 JsonElement。
        // 映射 + 环境变量展开为 CPU 工作,每次现算,保证环境变量即时生效。
        var channelSet = await _cache.GetOrCreateAsync(
            CacheKeys.RouteChannels(ownerUsername ?? string.Empty),
            () => LoadChannelSet(ownerUsername),
            RouteCacheTtl);
        var channels = channelSet?.Channels ?? new List<Channel>();
        var owners = channelSet?.OwnerNames ?? new Dictionary<Guid, string>();

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

    private Task<CachedChannelSet?> LoadChannelSet(string? ownerUsername)
    {
        var normalizedOwnerUsername = string.IsNullOrWhiteSpace(ownerUsername)
            ? string.Empty
            : ownerUsername.Trim();

        var query = _channelRepository.TableNoTracking;
        if (normalizedOwnerUsername.Length > 0)
        {
            // 按 owner username 过滤:先查 User 拿 UserId
            var ownerUserId = _userRepository.TableNoTracking
                .Where(u => u.Username == normalizedOwnerUsername)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefault();
            if (!ownerUserId.HasValue)
            {
                // owner 不存在:不缓存(null),下次仍回源,避免新用户创建后读到陈旧空集。
                return Task.FromResult<CachedChannelSet?>(null);
            }
            query = query.Where(channel => channel.OwnerUserId == ownerUserId.Value);
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
                .Select(u => new { u.Id, u.Username })
                .ToDictionary(u => u.Id, u => u.Username)
            : new Dictionary<Guid, string>();

        return Task.FromResult<CachedChannelSet?>(new CachedChannelSet(channels, owners));
    }

    private sealed record CachedChannelSet(List<Channel> Channels, Dictionary<Guid, string> OwnerNames);

    private static ChannelDto MapToChannelDto(Channel channel, string ownerUsername)
    {
        return new ChannelDto(
            channel.Id,
            channel.OwnerUserId,
            ownerUsername,
            channel.Position,
            channel.Name,
            channel.GroupName,
            channel.Type,
            channel.BaseUrl,
            channel.ApiKey,
            channel.AuthMode,
            DeserializeObject(channel.HeadersJson),
            channel.TimeoutSeconds,
            channel.CircuitBreakDurationSeconds,
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
            ["circuit_break_duration_seconds"] = channel.CircuitBreakDurationSeconds,
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

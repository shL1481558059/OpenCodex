using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.Caching;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Caching;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using StackExchange.Redis;

namespace OpenCodex.Core.Services;

public sealed class ModelCatalogService : IModelCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan PricingCacheTtl = TimeSpan.FromSeconds(60);

    /// 导入导出文档版本。v2 起携带峰谷字段,导入仍兼容 v1。
    internal const int ModelCatalogDocumentVersion = 2;

    private static int _localPricingVersion;
    private static int _lastKnownRedisPricingVersion;
    private static int _pendingRedisPricingVersionBump;

    private readonly IRepository<ModelProvider> _providers;
    private readonly IRepository<ModelInfo> _models;
    private readonly IRepository<ChannelModelInfo> _channelModels;
    private readonly IRepository<ModelPricingPlan> _plans;
    private readonly IRepository<ModelPricingRule> _rules;
    private readonly IRepository<ChannelModelMapping> _mappings;
    private readonly IRepository<Channel> _channels;
    private readonly IWorkContext _workContext;
    private readonly ICacheService _cache;
    private readonly IRedisConnectionProvider? _redis;
    private readonly IOpenCodexDbContext _dbContext;

    private sealed class ProxyModelCatalogEntry
    {
        public ProxyModelCatalogEntry(
            ProxyModelCapabilityDto route,
            ChannelModelInfo? channelModel,
            string baseDisplayName,
            Dictionary<string, object?> payload)
        {
            Route = route;
            ChannelModel = channelModel;
            BaseDisplayName = baseDisplayName;
            Payload = payload;
        }

        public ProxyModelCapabilityDto Route { get; }

        public ChannelModelInfo? ChannelModel { get; }

        public string BaseDisplayName { get; }

        public Dictionary<string, object?> Payload { get; }
    }

    public ModelCatalogService(
        IRepository<ModelProvider> providers,
        IRepository<ModelInfo> models,
        IRepository<ChannelModelInfo> channelModels,
        IRepository<ModelPricingPlan> plans,
        IRepository<ModelPricingRule> rules,
        IRepository<ChannelModelMapping> mappings,
        IRepository<Channel> channels,
        IWorkContext workContext,
        ICacheService cache,
        IRedisConnectionProvider? redis = null,
        IOpenCodexDbContext? dbContext = null)
    {
        _providers = providers;
        _models = models;
        _channelModels = channelModels;
        _plans = plans;
        _rules = rules;
        _mappings = mappings;
        _channels = channels;
        _workContext = workContext;
        _cache = cache;
        _redis = redis;
        _dbContext = dbContext
            ?? (SharedContext(
                    providers,
                    models,
                    channelModels,
                    plans,
                    rules,
                    mappings,
                    channels)
                ?? throw new InvalidOperationException(
                    "Model catalog repositories must use the same DbContext"));
    }

    public ApiOpResult<ModelProviderListResponse> ListProviders(bool includeDisabled = false)
    {
        var providers = _providers.TableNoTracking
            .Where(provider => includeDisabled || provider.Enabled)
            .OrderBy(provider => provider.SortOrder)
            .ThenBy(provider => provider.Code)
            .AsEnumerable()
            .Select(ToProviderResponse)
            .ToList();

        return ApiOpResult<ModelProviderListResponse>.Succeed(new ModelProviderListResponse(providers));
    }

    public ApiOpResult<ModelProviderResponsePayload> CreateProvider(ModelProviderUpsertRequest request)
    {
        try
        {
            var now = UnixTimeSeconds();
            var code = NormalizeProviderCodeRequired(request.Code);
            if (_providers.TableNoTracking.Any(provider => provider.Code == code))
            {
                return ProviderValidationFailure("provider_code already exists");
            }

            var provider = new ModelProvider
            {
                Code = code,
                Name = DisplayName(request.Name, code),
                Enabled = request.Enabled,
                SortOrder = request.SortOrder > 0 ? request.SortOrder : NextProviderSortOrder(),
                Source = ModelCatalogSources.Manual,
                CreatedAt = now,
                UpdatedAt = now
            };
            _providers.Insert(provider);

            return ApiOpResult<ModelProviderResponsePayload>.Succeed(
                new ModelProviderResponsePayload(ToProviderResponse(provider)));
        }
        catch (ArgumentException exception)
        {
            return ProviderValidationFailure(exception.Message);
        }
    }

    public ApiOpResult<ModelProviderResponsePayload> UpdateProvider(Guid id, ModelProviderUpsertRequest request)
    {
        try
        {
            if (id == Guid.Empty)
            {
                return ProviderValidationFailure("provider id is required");
            }

            var provider = _providers.Table.FirstOrDefault(p => p.Id == id);
            if (provider is null)
            {
                return ApiOpResult<ModelProviderResponsePayload>.Fail(404, "provider not found");
            }

            var code = NormalizeProviderCodeRequired(request.Code);
            if (_providers.TableNoTracking.Any(p => p.Code == code && p.Id != id))
            {
                return ProviderValidationFailure("provider_code already exists");
            }

            provider.Code = code;
            provider.Name = DisplayName(request.Name, code);
            provider.Enabled = request.Enabled;
            provider.SortOrder = request.SortOrder > 0 ? request.SortOrder : provider.SortOrder;
            provider.UpdatedAt = UnixTimeSeconds();
            _providers.Update(provider);

            return ApiOpResult<ModelProviderResponsePayload>.Succeed(
                new ModelProviderResponsePayload(ToProviderResponse(provider)));
        }
        catch (ArgumentException exception)
        {
            return ProviderValidationFailure(exception.Message);
        }
    }

    public ApiOpResult<ModelProviderResponsePayload> DeleteProvider(Guid id)
    {
        if (id == Guid.Empty)
        {
            return ProviderValidationFailure("provider id is required");
        }

        var provider = _providers.Table.FirstOrDefault(p => p.Id == id);
        if (provider is null)
        {
            return ApiOpResult<ModelProviderResponsePayload>.Fail(404, "provider not found");
        }

        // 检查是否有关联的模型
        var hasModels = _models.TableNoTracking.Any(m => m.ProviderId == id);
        if (hasModels)
        {
            return ProviderValidationFailure("cannot delete provider with existing models; disable or reassign models first");
        }

        _providers.Delete(provider);
        return ApiOpResult<ModelProviderResponsePayload>.Succeed(
            new ModelProviderResponsePayload(ToProviderResponse(provider)));
    }

    public ApiOpResult<ModelInfoListResponse> ListModels(
        string? query,
        string? providerCode,
        bool? enabled)
    {
        var providerById = ProviderMap();
        var normalizedProvider = Normalize(providerCode).ToLowerInvariant();
        var providerIds = normalizedProvider.Length == 0
            ? null
            : providerById.Values
                .Where(provider => string.Equals(provider.Code, normalizedProvider, StringComparison.OrdinalIgnoreCase))
                .Select(provider => provider.Id)
                .ToHashSet();

        var normalizedQuery = Normalize(query).ToLowerInvariant();

        var modelQuery = _models.TableNoTracking
            .Where(model => model.Scope == ModelInfoScopes.Global && model.ChannelId == null);
        if (providerIds is not null)
        {
            modelQuery = modelQuery.Where(model => providerIds.Contains(model.ProviderId));
        }

        if (enabled.HasValue)
        {
            modelQuery = modelQuery.Where(model => model.Enabled == enabled.Value);
        }

        var models = modelQuery
            .OrderBy(model => model.ModelKey)
            .ToList();

        if (normalizedQuery.Length > 0)
        {
            models = models
                .Where(model =>
                    model.ModelKey.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || model.DisplayName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || model.MatchPattern.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || ProviderText(providerById, model.ProviderId).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var plansByModel = PlansByModelId(models.Select(model => model.Id).ToList());
        var rulesByPlan = RulesByPlanIds(plansByModel.Values.Select(plan => plan.Id).ToList());
        return ApiOpResult<ModelInfoListResponse>.Succeed(new ModelInfoListResponse(
            models
                .Select(model => ToModelResponse(
                    model,
                    providerById,
                    plansByModel.TryGetValue(model.Id, out var plan) ? plan : null,
                    rulesByPlan))
                .ToList()));
    }

    public IReadOnlyList<Dictionary<string, object?>> BuildProxyModelCatalog(
        IReadOnlyList<ProxyModelCapabilityDto> routedModels)
    {
        if (routedModels.Count == 0)
        {
            return [];
        }

        var routes = routedModels
            .Where(route => !string.IsNullOrWhiteSpace(route.Model))
            .GroupBy(route => route.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (routes.Count == 0)
        {
            return [];
        }

        var channelIds = routes
            .Where(route => route.ChannelId.HasValue)
            .Select(route => route.ChannelId!.Value)
            .Distinct()
            .ToList();
        var upstreamModels = routes
            .Select(route => Normalize(string.IsNullOrWhiteSpace(route.UpstreamModel)
                ? route.Model
                : route.UpstreamModel))
            .Where(model => model.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var channelModels = _channelModels.TableNoTracking
            .Where(model => channelIds.Contains(model.ChannelId) && model.Enabled)
            .AsEnumerable()
            .GroupBy(model => ChannelModelKey(model.ChannelId, model.UpstreamModel), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(model => model.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var globalModels = ResolveGlobalModels(upstreamModels);
        var entries = routes
            .Select(route => BuildProxyModelEntry(route, channelModels, globalModels))
            .ToList();

        var displayNameCounts = entries
            .GroupBy(entry => entry.BaseDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var displayName = entry.BaseDisplayName;
            if (displayNameCounts[entry.BaseDisplayName] > 1
                && entry.ChannelModel is not null
                && !string.IsNullOrWhiteSpace(entry.Route.ChannelName))
            {
                displayName = $"{entry.Route.ChannelName}/{displayName}";
            }

            entry.Payload["display_name"] = displayName;
            entry.Payload["slug"] = entry.Route.Model;
        }

        return entries.Select(entry => entry.Payload).ToList();
    }

    private ProxyModelCatalogEntry BuildProxyModelEntry(
        ProxyModelCapabilityDto route,
        IReadOnlyDictionary<string, ChannelModelInfo> channelModels,
        IReadOnlyDictionary<string, ModelInfo?> globalModels)
    {
        var upstreamModel = Normalize(string.IsNullOrWhiteSpace(route.UpstreamModel)
            ? route.Model
            : route.UpstreamModel);
        channelModels.TryGetValue(
            ChannelModelKey(route.ChannelId, upstreamModel),
            out var channelModel);
        globalModels.TryGetValue(upstreamModel, out var globalModel);

        var globalCatalog = globalModel is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : DeserializeObject(globalModel.CatalogJson);
        var channelCatalog = channelModel is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : DeserializeObject(channelModel.CatalogJson);
        var catalog = MergeDictionaries(globalCatalog, channelCatalog);
        var globalCapabilities = globalModel is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : DeserializeObject(globalModel.CapabilitiesJson);
        var channelCapabilities = channelModel is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : DeserializeObject(channelModel.CapabilitiesJson);
        var capabilities = MergeDictionaries(globalCapabilities, channelCapabilities);

        var displayName = FirstNonEmpty(
            ReadString(channelCatalog, "display_name"),
            channelModel?.DisplayName,
            ReadString(globalCatalog, "display_name"),
            globalModel?.DisplayName,
            route.Model);
        var description = FirstNonEmpty(
            ReadString(channelCatalog, "description"),
            channelModel?.Description,
            ReadString(globalCatalog, "description"),
            globalModel?.Description,
            $"OpenCodex routed model: {route.Model}.");
        var supportsImage = ReadBoolean(channelCapabilities, "supports_image")
            ?? ReadBoolean(globalCapabilities, "supports_image")
            ?? route.SupportsImage;
        var contextWindow = ReadPositiveLong(channelCatalog, "context_window")
            ?? ReadPositiveLong(channelCapabilities, "context_window")
            ?? ReadPositiveLong(globalCatalog, "context_window")
            ?? ReadPositiveLong(globalCapabilities, "context_window")
            ?? 256000;

        // 客户端目录只暴露 Codex/OpenAI 客户端需要的能力字段。
        // 定价与内部标识只在管理台接口和计费链路使用，不随 /models 下发给访问 Key 持有者。
        ApplyProxyCatalogDefaults(catalog, route, displayName, description, supportsImage, contextWindow);
        var payload = CloneDictionary(catalog);
        payload["slug"] = route.Model;
        payload["display_name"] = displayName;
        payload["description"] = description;

        return new ProxyModelCatalogEntry(route, channelModel, displayName, payload);
    }

    private static void ApplyProxyCatalogDefaults(
        Dictionary<string, object?> catalog,
        ProxyModelCapabilityDto route,
        string displayName,
        string description,
        bool supportsImage,
        long contextWindow)
    {
        catalog["slug"] = route.Model;
        catalog["display_name"] = displayName;
        catalog["description"] = description;
        catalog["visibility"] = catalog.TryGetValue("visibility", out var visibility)
            ? visibility
            : "list";
        catalog["supported_in_api"] = catalog.TryGetValue("supported_in_api", out var supportedInApi)
            ? supportedInApi
            : true;
        catalog["shell_type"] = catalog.TryGetValue("shell_type", out var shellType)
            ? shellType
            : "shell_command";
        catalog["priority"] = catalog.TryGetValue("priority", out var priority)
            ? priority
            : 100;
        catalog["apply_patch_tool_type"] = catalog.TryGetValue("apply_patch_tool_type", out var applyPatchToolType)
            ? applyPatchToolType
            : "freeform";
        catalog["web_search_tool_type"] = catalog.TryGetValue("web_search_tool_type", out var webSearchToolType)
            ? webSearchToolType
            : "text";
        catalog["reasoning_summary_format"] = catalog.TryGetValue("reasoning_summary_format", out var summaryFormat)
            ? summaryFormat
            : "text";
        // codex 只接受 auto/concise/detailed/none,数据库里历史数据存过 "short",
        // 透传会让整份 /v1/models 缓存解析失败,统一归一化到 auto。
        catalog["default_reasoning_summary"] = NormalizeDefaultReasoningSummary(
            catalog.TryGetValue("default_reasoning_summary", out var defaultSummary)
                ? defaultSummary
                : null);
        catalog["support_verbosity"] = catalog.TryGetValue("support_verbosity", out var supportVerbosity)
            ? supportVerbosity
            : true;
        catalog["default_verbosity"] = catalog.TryGetValue("default_verbosity", out var defaultVerbosity)
            ? defaultVerbosity
            : "medium";
        catalog["input_modalities"] = catalog.TryGetValue("input_modalities", out var inputModalities)
            ? inputModalities
            : supportsImage
                ? new List<object?> { "text", "image" }
                : new List<object?> { "text" };
        catalog["supports_image_detail_original"] = supportsImage;
        catalog["supports_parallel_tool_calls"] = catalog.TryGetValue("supports_parallel_tool_calls", out var parallel)
            ? parallel
            : true;
        catalog["supports_reasoning_summaries"] = catalog.TryGetValue("supports_reasoning_summaries", out var summaries)
            ? summaries
            : true;
        catalog["additional_speed_tiers"] = catalog.TryGetValue("additional_speed_tiers", out var speedTiers)
            ? speedTiers
            : new List<object?> { "fast" };
        catalog["context_window"] = contextWindow;
        catalog["max_context_window"] = contextWindow;

        // 没有配置思考档位的模型也必须给出可选档位，否则客户端无法选择推理强度。
        if (!catalog.TryGetValue("supported_reasoning_levels", out var configuredLevels)
            || configuredLevels is not IEnumerable<object?> configuredLevelItems
            || !configuredLevelItems.Any())
        {
            catalog["supported_reasoning_levels"] = DefaultReasoningLevels();
        }

        if (!catalog.ContainsKey("default_reasoning_level"))
        {
            catalog["default_reasoning_level"] = "medium";
        }

        if (catalog.TryGetValue("truncation_policy", out var policyValue)
            && policyValue is Dictionary<string, object?> policy)
        {
            policy["limit"] = contextWindow;
        }
        else
        {
            catalog["truncation_policy"] = new Dictionary<string, object?>
            {
                ["mode"] = "tokens",
                ["limit"] = contextWindow
            };
        }

        if (catalog.TryGetValue("supported_reasoning_levels", out var levelsValue)
            && levelsValue is IEnumerable<object?> levels)
        {
            var efforts = levels
                .Select(level => level as IReadOnlyDictionary<string, object?>)
                .Where(level => level is not null)
                .Select(level => ReadString(level!, "effort"))
                .Where(effort => effort.Length > 0)
                .ToList();
            if (efforts.Count > 0)
            {
                var defaultLevel = ReadString(catalog, "default_reasoning_level");
                if (!efforts.Contains(defaultLevel, StringComparer.OrdinalIgnoreCase))
                {
                    catalog["default_reasoning_level"] = efforts[0];
                }
            }
        }

        ApplyCodexRequiredContract(catalog, supportsImage);
    }

    private static void ApplyCodexRequiredContract(
        Dictionary<string, object?> catalog,
        bool supportsImage)
    {
        // codex 客户端要求这些字段必须存在,缺失会让整份 /v1/models 响应在解析阶段被丢弃。
        catalog.TryAdd("experimental_supported_tools", new List<object?>());
        catalog.TryAdd("service_tiers", new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["id"] = "priority",
                ["name"] = "Fast",
                ["description"] = "1.5x speed, increased usage"
            }
        });
        catalog.TryAdd("supports_search_tool", true);
        catalog.TryAdd("use_responses_lite", false);
        catalog.TryAdd("node_repl_disabled", false);
        catalog.TryAdd("node_repl_auto_review_required", false);
        catalog.TryAdd("include_apps_usage_instructions", true);
        catalog.TryAdd("include_plugin_usage_instructions", true);
        catalog.TryAdd("include_skills_usage_instructions", true);
        catalog.TryAdd("effective_context_window_percent", 100);
        catalog.TryAdd("availability_nux", null);
        catalog.TryAdd("upgrade", null);

        // codex 客户端只接受 text / image / audio 三种模态,出现 video 会导致整份响应解析失败;
        // 且 supportsImage 只能推导出图片能力,不应顺带声明音频或视频。
        if (!catalog.TryGetValue("input_modalities", out var modalities)
            || modalities is not IEnumerable<object?> modalityItems
            || !modalityItems.Any())
        {
            catalog["input_modalities"] = supportsImage
                ? new List<object?> { "text", "image" }
                : new List<object?> { "text" };
        }

        // codex 客户端语义校验要求每个模型至少提供 base_instructions 或
        // model_messages.instructions_template,二者都缺失会导致整份响应校验失败。
        if (!catalog.ContainsKey("base_instructions")
            && !catalog.ContainsKey("model_messages"))
        {
            catalog["base_instructions"] = CodexModelInstructions.BaseInstructions;
            catalog["model_messages"] = CodexModelInstructions.ModelMessages;
        }
    }

    private static object NormalizeDefaultReasoningSummary(object? value)
    {
        if (value is string text
            && text is "auto" or "concise" or "detailed" or "none")
        {
            return text;
        }

        return "auto";
    }

    private static List<object?> DefaultReasoningLevels()
    {
        return
        [
            new Dictionary<string, object?>
            {
                ["effort"] = "low",
                ["description"] = "Quick responses with lighter reasoning"
            },
            new Dictionary<string, object?>
            {
                ["effort"] = "medium",
                ["description"] = "Balances speed and reasoning depth for everyday tasks"
            },
            new Dictionary<string, object?>
            {
                ["effort"] = "high",
                ["description"] = "Greater reasoning depth for complex problems"
            },
            new Dictionary<string, object?>
            {
                ["effort"] = "xhigh",
                ["description"] = "Extra high reasoning depth for extremely complex logic"
            }
        ];
    }

    private static Dictionary<string, object?> CloneDictionary(
        IReadOnlyDictionary<string, object?> source)
    {
        return source.ToDictionary(
            pair => pair.Key,
            pair => CloneProxyCatalogValue(pair.Value),
            StringComparer.Ordinal);
    }

    private static object? CloneProxyCatalogValue(object? value)
    {
        if (value is JsonElement element)
        {
            return JsonRequestValue.Value(element);
        }

        if (value is IReadOnlyDictionary<string, object?> dictionary)
        {
            return CloneDictionary(dictionary);
        }

        if (value is IEnumerable<object?> values)
        {
            return values.Select(CloneProxyCatalogValue).ToList();
        }

        return value;
    }

    private static Dictionary<string, object?> MergeDictionaries(
        IReadOnlyDictionary<string, object?> baseValues,
        IReadOnlyDictionary<string, object?> overrides)
    {
        var result = CloneDictionary(baseValues);
        foreach (var pair in overrides)
        {
            result[pair.Key] = CloneProxyCatalogValue(pair.Value);
        }

        return result;
    }

    private static string ChannelModelKey(Guid? channelId, string upstreamModel)
    {
        return $"{channelId?.ToString() ?? string.Empty}|{Normalize(upstreamModel)}";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string ReadString(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        return source.TryGetValue(key, out var value) ? value?.ToString()?.Trim() ?? string.Empty : string.Empty;
    }

    private static bool? ReadBoolean(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        if (!source.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            bool boolean => boolean,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static long? ReadPositiveLong(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        if (!source.TryGetValue(key, out var value))
        {
            return null;
        }

        var number = value switch
        {
            int integer => integer,
            long longValue => longValue,
            double fraction => (long)fraction,
            decimal decimalValue => (long)decimalValue,
            string text when long.TryParse(text, out var parsed) => parsed,
            _ => 0
        };
        return number > 0 ? number : null;
    }

    public ApiOpResult<ModelInfoResponsePayload> ReadModelInfoById(Guid id)
    {
        if (id == Guid.Empty)
        {
            return ApiOpResult<ModelInfoResponsePayload>.Fail(400, "model id is required");
        }

        var providerById = ProviderMap();
        var model = _models.TableNoTracking.FirstOrDefault(m => m.Id == id);
        if (model is null)
        {
            return ApiOpResult<ModelInfoResponsePayload>.Fail(404, "model not found");
        }

        var plansByModel = PlansByModelId([model.Id]);
        var rulesByPlan = RulesByPlanIds(plansByModel.Values.Select(plan => plan.Id).ToList());
        return ApiOpResult<ModelInfoResponsePayload>.Succeed(
            new ModelInfoResponsePayload(ToModelResponse(
                model,
                providerById,
                plansByModel.TryGetValue(model.Id, out var plan) ? plan : null,
                rulesByPlan)));
    }

    public ApiOpResult<ModelInfoResponsePayload> CreateModel(ModelInfoCreateRequest request)
    {
        try
        {
            ValidatePricingRequest(request.Pricing);
            var now = UnixTimeSeconds();
            var provider = ResolveProvider(request.ProviderId, request.ProviderCode);
            var modelKey = NormalizeRequired(request.ModelKey, "model_key");
            var scope = ModelInfoScopes.Global;
            Guid? channelId = null;

            if (ModelExists(scope, channelId, modelKey, null))
            {
                return ModelValidationFailure("model_key already exists");
            }

            var model = new ModelInfo
            {
                Scope = scope,
                ProviderId = provider.Id,
                ChannelId = channelId,
                ModelKey = modelKey,
                DisplayName = DisplayName(request.DisplayName, modelKey),
                Description = Normalize(request.Description),
                MatchType = NormalizeMatchType(request.MatchType),
                MatchPattern = NormalizeMatchPattern(request.MatchPattern, modelKey),
                CatalogJson = SerializeObject(request.Catalog),
                CapabilitiesJson = SerializeObject(request.Capabilities),
                Enabled = request.Enabled,
                Source = ModelCatalogSources.Manual,
                CreatedAt = now,
                UpdatedAt = now
            };

            _models.Insert(model);
            ReplacePricing(model, request.Pricing, ModelCatalogSources.Manual, now);
            BumpPricingVersion();

            return ApiOpResult<ModelInfoResponsePayload>.Succeed(
                new ModelInfoResponsePayload(ToModelResponseForSingle(model, ProviderMap())));
        }
        catch (ArgumentException exception)
        {
            return ModelValidationFailure(exception.Message);
        }
    }

    public ApiOpResult<ModelInfoResponsePayload> UpdateModel(Guid id, ModelInfoUpdateRequest request)
    {
        try
        {
            ValidatePricingRequest(request.Pricing);
            var model = _models.Table.FirstOrDefault(item => item.Id == id);
            if (model is null)
            {
                return ApiOpResult<ModelInfoResponsePayload>.Fail(404, "model not found");
            }

            var oldChannelId = model.ChannelId;
            var provider = ResolveProvider(request.ProviderId, request.ProviderCode);
            var modelKey = NormalizeRequired(request.ModelKey, "model_key");
            var scope = ModelInfoScopes.Global;
            Guid? channelId = null;

            if (ModelExists(scope, channelId, modelKey, id))
            {
                return ModelValidationFailure("model_key already exists");
            }

            var now = UnixTimeSeconds();
            model.Scope = scope;
            model.ProviderId = provider.Id;
            model.ChannelId = channelId;
            model.ModelKey = modelKey;
            model.DisplayName = DisplayName(request.DisplayName, modelKey);
            model.Description = Normalize(request.Description);
            model.MatchType = NormalizeMatchType(request.MatchType);
            model.MatchPattern = NormalizeMatchPattern(request.MatchPattern, modelKey);
            model.CatalogJson = SerializeObject(request.Catalog);
            model.CapabilitiesJson = SerializeObject(request.Capabilities);
            model.Enabled = request.Enabled;
            model.Source = ModelCatalogSources.Manual;
            model.UpdatedAt = now;
            _models.Update(model);

            if (oldChannelId != channelId)
            {
                RemovePlans(model.Id, oldChannelId);
            }
            ReplacePricing(model, request.Pricing, ModelCatalogSources.Manual, now);
            BumpPricingVersion();

            return ApiOpResult<ModelInfoResponsePayload>.Succeed(
                new ModelInfoResponsePayload(ToModelResponseForSingle(model, ProviderMap())));
        }
        catch (ArgumentException exception)
        {
            return ModelValidationFailure(exception.Message);
        }
    }

    public ApiOpResult<ModelInfoResponsePayload> DeleteModel(Guid id)
    {
        var model = _models.Table.FirstOrDefault(item => item.Id == id);
        if (model is null)
        {
            return ApiOpResult<ModelInfoResponsePayload>.Fail(404, "model not found");
        }

        if (model.Enabled)
        {
            // 启用状态：执行停用（软删除）
            model.Enabled = false;
            model.UpdatedAt = UnixTimeSeconds();
            _models.Update(model);
            BumpPricingVersion();
            return ApiOpResult<ModelInfoResponsePayload>.Succeed(
                new ModelInfoResponsePayload(ToModelResponseForSingle(model, ProviderMap())));
        }

        // 停用状态：执行真正删除（硬删除）
        RemovePlans(model.Id, model.ChannelId);
        _models.Delete(model);
        BumpPricingVersion();
        return ApiOpResult<ModelInfoResponsePayload>.Succeed(null);
    }

    public ApiOpResult<ModelBatchActionResult> BatchModels(ModelBatchActionRequest request)
    {
        var action = Normalize(request.Action).ToLowerInvariant();
        if (action is not ("enable" or "disable" or "delete"))
        {
            return ApiOpResult<ModelBatchActionResult>.Fail(
                400,
                "action must be one of 'enable', 'disable' or 'delete'");
        }

        var ids = (request.Ids ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return ApiOpResult<ModelBatchActionResult>.Fail(400, "ids is required");
        }

        var models = _models.Table
            .Where(model => model.Scope == ModelInfoScopes.Global
                && model.ChannelId == null
                && ids.Contains(model.Id))
            .ToList();
        if (models.Count == 0)
        {
            return ApiOpResult<ModelBatchActionResult>.Fail(404, "no model found");
        }

        var now = UnixTimeSeconds();
        var updatedIds = new List<Guid>();
        var deletedIds = new List<Guid>();
        var errors = new List<string>();

        using var transaction = _dbContext.Database.BeginTransaction();
        try
        {
            foreach (var model in models)
            {
                if (action == "enable")
                {
                    if (!model.Enabled)
                    {
                        model.Enabled = true;
                        model.UpdatedAt = now;
                        _models.Update(model);
                        updatedIds.Add(model.Id);
                    }
                }
                else if (action == "disable")
                {
                    if (model.Enabled)
                    {
                        model.Enabled = false;
                        model.UpdatedAt = now;
                        _models.Update(model);
                        updatedIds.Add(model.Id);
                    }
                }
                else
                {
                    if (model.Enabled)
                    {
                        errors.Add($"model '{model.ModelKey}' is enabled; disable it first");
                    }
                    else
                    {
                        RemovePlans(model.Id, model.ChannelId);
                        _models.Delete(model);
                        deletedIds.Add(model.Id);
                    }
                }
            }

            _dbContext.SaveChanges();
            transaction.Commit();
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
        {
            transaction.Rollback();
            return ApiOpResult<ModelBatchActionResult>.Fail(500, exception.Message);
        }

        if (updatedIds.Count > 0 || deletedIds.Count > 0)
        {
            BumpPricingVersion();
        }

        return ApiOpResult<ModelBatchActionResult>.Succeed(
            new ModelBatchActionResult(action, updatedIds, deletedIds, errors));
    }

    public ApiOpResult<ModelCatalogTransferDocument> ExportModelCatalog()
    {
        var providerById = _providers.TableNoTracking
            .Select(provider => new ProviderLookup(provider.Id, provider.Code, provider.Name))
            .ToDictionary(provider => provider.Id);
        var plansByModel = _plans.TableNoTracking
            .Where(plan => plan.ModelInfoId != null && plan.ChannelModelInfoId == null && plan.ChannelId == null)
            .AsEnumerable()
            .GroupBy(plan => plan.ModelInfoId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(plan => plan.UpdatedAt).First());
        var rulesByPlan = _rules.TableNoTracking
            .AsEnumerable()
            .GroupBy(rule => rule.PricingPlanId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(rule => rule.BillingItem, StringComparer.Ordinal)
                    .ThenBy(rule => rule.BillingMode, StringComparer.Ordinal)
                    .ToList());

        var providers = _providers.TableNoTracking
            .OrderBy(provider => provider.SortOrder)
            .AsEnumerable()
            .OrderBy(provider => provider.SortOrder)
            .ThenBy(provider => provider.Code, StringComparer.Ordinal)
            .Select(ToProviderTransfer)
            .ToList();

        var models = _models.TableNoTracking
            .Where(model => model.Scope == ModelInfoScopes.Global && model.ChannelId == null)
            .AsEnumerable()
            .Select(model => ToModelTransfer(
                model,
                providerById.TryGetValue(model.ProviderId, out var provider) ? provider.Code : string.Empty,
                plansByModel.TryGetValue(model.Id, out var plan) ? plan : null,
                plan => rulesByPlan.TryGetValue(plan.Id, out var rules) ? rules : []))
            .ToList();

        return ApiOpResult<ModelCatalogTransferDocument>.Succeed(new ModelCatalogTransferDocument
        {
            Type = "model_catalog",
            Version = ModelCatalogDocumentVersion,
            ExportedAt = DateTimeOffset.UtcNow.ToString("O"),
            Providers = providers,
            Models = models
        });
    }

    public ApiOpResult<ModelCatalogImportResult> ImportModelCatalog(
        ModelCatalogTransferDocument document,
        bool dryRun)
    {
        return ImportModelCatalog(document, dryRun, new ModelCatalogImportOptions
        {
            SkipExistingModels = false,
            SkipExistingProviders = false,
            PreserveLocalEnabled = false,
            KeepLocalPricingWhenRemoteNull = false,
            Source = ModelCatalogSources.Manual
        });
    }

    public ApiOpResult<ModelCatalogImportResult> ImportModelCatalog(
        ModelCatalogTransferDocument document,
        bool dryRun,
        ModelCatalogImportOptions options)
    {
        if (document is null)
        {
            return ImportFailure("request body is required");
        }

        var validation = ValidateImportDocument(document);
        if (validation.Count > 0)
        {
            return ImportFailure(string.Join("; ", validation));
        }

        var existingProviders = _providers.TableNoTracking
            .AsEnumerable()
            .GroupBy(provider => provider.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        if (existingProviders.Any(pair => pair.Value.Count > 1))
        {
            var duplicate = existingProviders.First(pair => pair.Value.Count > 1).Key;
            return ImportFailure($"provider_code '{duplicate}' is duplicated in the database");
        }

        var existingModels = _models.TableNoTracking
            .Where(model => model.Scope == ModelInfoScopes.Global && model.ChannelId == null)
            .AsEnumerable()
            .GroupBy(model => model.ModelKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        if (existingModels.Any(pair => pair.Value.Count > 1))
        {
            var duplicate = existingModels.First(pair => pair.Value.Count > 1).Key;
            return ImportFailure($"model_key '{duplicate}' is duplicated in the database");
        }

        var plansByModelId = _plans.TableNoTracking
            .Where(plan => plan.ModelInfoId != null && plan.ChannelModelInfoId == null && plan.ChannelId == null)
            .AsEnumerable()
            .GroupBy(plan => plan.ModelInfoId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(plan => plan.UpdatedAt).First());
        var rulesByPlanId = _rules.TableNoTracking
            .AsEnumerable()
            .GroupBy(rule => rule.PricingPlanId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(rule => rule.BillingItem, StringComparer.Ordinal)
                    .ThenBy(rule => rule.BillingMode, StringComparer.Ordinal)
                    .ToList());

        var errors = new List<string>();
        var providerPlans = new List<(ModelCatalogProviderTransfer Transfer, ModelProvider? Entity)>();
        foreach (var item in document.Providers)
        {
            try
            {
                var code = NormalizeProviderCodeRequired(item.Code);
                existingProviders.TryGetValue(code, out var matches);
                var existing = matches?.SingleOrDefault();
                providerPlans.Add((item, existing));
            }
            catch (ArgumentException exception)
            {
                errors.Add(exception.Message);
                providerPlans.Add((item, null));
            }
        }

        var modelPlans = new List<(ModelCatalogModelTransfer Transfer, ModelInfo? Entity, ModelProvider Provider)>();
        var providerIdByCode = new Dictionary<string, ModelProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in existingProviders.Values.SelectMany(providers => providers))
        {
            providerIdByCode[provider.Code] = provider;
        }

        foreach (var item in document.Models)
        {
            ModelProvider? provider = null;
            try
            {
                var providerCode = NormalizeProviderCodeRequired(item.ProviderCode);
                if (existingProviders.TryGetValue(providerCode, out var matches))
                {
                    provider = matches.SingleOrDefault();
                }

                if (provider is null)
                {
                    var transferCode = document.Providers
                        .FirstOrDefault(candidate => string.Equals(
                            NormalizeProviderCode(candidate.Code),
                            providerCode,
                            StringComparison.OrdinalIgnoreCase));
                    if (transferCode is not null)
                    {
                        provider = new ModelProvider
                        {
                            Code = NormalizeProviderCodeRequired(transferCode.Code),
                            Name = DisplayName(transferCode.Name, transferCode.Code),
                            Enabled = transferCode.Enabled,
                            SortOrder = transferCode.SortOrder
                        };
                        providerIdByCode[provider.Code] = provider;
                    }
                }

                if (provider is null)
                {
                    throw new ArgumentException($"provider_code '{item.ProviderCode}' is invalid", nameof(item.ProviderCode));
                }

                var modelKey = NormalizeRequired(item.ModelKey, "model_key");
                existingModels.TryGetValue(modelKey, out var modelMatches);
                var existing = modelMatches?.SingleOrDefault();
                ValidateImportModel(item);
                modelPlans.Add((item, existing, provider));
            }
            catch (ArgumentException exception)
            {
                errors.Add(exception.Message);
                modelPlans.Add((item, null, provider ?? new ModelProvider()));
            }
        }

        if (errors.Count > 0)
        {
            return ImportFailure(string.Join("; ", errors), errors);
        }

        var providerCounts = ImportCounts(
            providerPlans.Select(plan => plan.Entity is null),
            providerPlans.Select(plan => plan.Entity is null
                ? false
                : options.SkipExistingProviders || ProviderUnchanged(plan.Entity, plan.Transfer)));
        // When SkipExistingModels is on, existing models are "skipped" rather than
        // candidates for update/unchanged. Collect key lists for the response DTO.
        var createdModelKeys = new List<string>();
        var skippedModelKeys = new List<string>();
        var overwrittenModelKeys = new List<string>();
        var modelCreated = new List<bool>();
        var modelUnchanged = new List<bool>();
        foreach (var (transfer, entity, provider) in modelPlans)
        {
            if (entity is null)
            {
                createdModelKeys.Add(NormalizeRequired(transfer.ModelKey, "model_key"));
                modelCreated.Add(true);
                modelUnchanged.Add(false);
            }
            else if (options.SkipExistingModels)
            {
                skippedModelKeys.Add(entity.ModelKey);
                modelCreated.Add(false);
                modelUnchanged.Add(false);
            }
            else
            {
                var unchanged = ModelUnchanged(
                    entity,
                    provider.Id,
                    transfer,
                    plansByModelId,
                    rulesByPlanId);
                if (!unchanged)
                {
                    overwrittenModelKeys.Add(entity.ModelKey);
                }
                modelCreated.Add(false);
                modelUnchanged.Add(unchanged);
            }
        }
        var modelCounts = ImportCounts(modelCreated, modelUnchanged);
        var skipped = skippedModelKeys.Count;

        var pricingDeleted = options.KeepLocalPricingWhenRemoteNull
            ? 0
            : modelPlans.Count(plan => plan.Entity is not null
                && plan.Transfer.Pricing is null
                && plansByModelId.ContainsKey(plan.Entity.Id));

        var result = new ModelCatalogImportResult
        {
            DryRun = dryRun,
            Mode = options.Source == ModelCatalogSources.Sync ? options.Source : null,
            Providers = providerCounts,
            Models = modelCounts,
            Skipped = skipped,
            CreatedModelKeys = createdModelKeys,
            SkippedModelKeys = skippedModelKeys,
            OverwrittenModelKeys = overwrittenModelKeys,
            PricingDeleted = pricingDeleted,
            ErrorCount = 0,
            Errors = []
        };
        if (dryRun)
        {
            return ApiOpResult<ModelCatalogImportResult>.Succeed(result);
        }

        var now = UnixTimeSeconds();
        using var transaction = _dbContext.Database.BeginTransaction();
        try
        {
            var trackedProvidersByCode = new Dictionary<string, ModelProvider>(StringComparer.OrdinalIgnoreCase);
            foreach (var (transfer, existing) in providerPlans)
            {
                var provider = _providers.Table
                    .AsEnumerable()
                    .FirstOrDefault(item => string.Equals(
                        item.Code,
                        NormalizeProviderCodeRequired(transfer.Code),
                        StringComparison.OrdinalIgnoreCase));
                if (provider is null)
                {
                    provider = new ModelProvider
                    {
                        Code = NormalizeProviderCodeRequired(transfer.Code),
                        Name = DisplayName(transfer.Name, transfer.Code),
                        Enabled = transfer.Enabled,
                       SortOrder = transfer.SortOrder,
                       Source = options.Source,
                       CreatedAt = now,
                       UpdatedAt = now
                   };
                   _providers.Insert(provider);
               }
               else
               {
                   if (!options.SkipExistingProviders)
                   {
                       provider.Name = DisplayName(transfer.Name, provider.Code);
                       provider.Enabled = transfer.Enabled;
                       provider.SortOrder = transfer.SortOrder;
                       provider.UpdatedAt = now;
                       _providers.Update(provider);
                   }
               }

               trackedProvidersByCode[provider.Code] = provider;
           }

           foreach (var (transfer, existing, _) in modelPlans)
           {
               // Skip existing models entirely when the option is set (incremental sync).
               var modelKey = NormalizeRequired(transfer.ModelKey, "model_key");
               if (options.SkipExistingModels)
               {
                   var exists = _models.TableNoTracking
                       .AsEnumerable()
                       .Any(item => item.Scope == ModelInfoScopes.Global
                           && item.ChannelId == null
                           && string.Equals(item.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase));
                   if (exists)
                   {
                       continue;
                   }
               }

               var providerCode = NormalizeProviderCodeRequired(transfer.ProviderCode);
               // Fix: was trackedProvidersByCode[providerCode] which threw KeyNotFoundException
               // when the provider existed in DB but wasn't in the document's providers list.
               if (!trackedProvidersByCode.TryGetValue(providerCode, out var provider) || provider is null)
               {
                   return ImportFailure(
                       $"provider_code '{transfer.ProviderCode}' is not found in the import document");
               }

               var model = _models.Table
                   .AsEnumerable()
                   .FirstOrDefault(item => item.Scope == ModelInfoScopes.Global
                       && item.ChannelId == null
                       && string.Equals(item.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase))
                   ?? new ModelInfo
                   {
                       Scope = ModelInfoScopes.Global,
                       ChannelId = null,
                       ModelKey = modelKey,
                       Source = options.Source,
                       CreatedAt = now
                   };

               model.ProviderId = provider.Id;
               model.DisplayName = DisplayName(transfer.DisplayName, modelKey);
               model.Description = Normalize(transfer.Description);
               model.MatchType = NormalizeMatchType(transfer.MatchType);
               model.MatchPattern = NormalizeMatchPattern(transfer.MatchPattern, modelKey);
               model.CatalogJson = SerializeObject(JsonRequestValue.Object(transfer.Catalog));
               model.CapabilitiesJson = SerializeObject(JsonRequestValue.Object(transfer.Capabilities));
               // PreserveLocalEnabled: never overwrite enabled on existing models;
               // new models always take the remote enabled value.
               var isNewModel = model.CreatedAt == now;
               if (isNewModel || !options.PreserveLocalEnabled)
               {
                   model.Enabled = transfer.Enabled;
               }
               model.Source = options.Source;
               model.UpdatedAt = now;
               _models.Update(model);

               ReplaceImportedPricing(model, transfer.Pricing, now, options);
           }

           _dbContext.SaveChanges();
           transaction.Commit();
           BumpPricingVersion();
           return ApiOpResult<ModelCatalogImportResult>.Succeed(result);
       }
       catch (Exception exception) when (exception is ArgumentException or DbUpdateException or InvalidOperationException)
       {
           transaction.Rollback();
           return ImportFailure(exception.Message);
       }
   }

   public ApiOpResult<ChannelModelInfoListResponse> ListChannelModelInfos(Guid channelId)
    {
        var channel = FindChannelInScope(channelId);
        if (channel is null)
        {
            return ApiOpResult<ChannelModelInfoListResponse>.Fail(404, "channel not found");
        }

        var providerById = ProviderMap();
        var upstreamModels = ListChannelUpstreamModels(channel);
        var overrides = new Dictionary<string, ChannelModelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in _channelModels.TableNoTracking
            .Where(model => model.ChannelId == channel.Id)
            .OrderByDescending(model => model.UpdatedAt)
            .AsEnumerable())
        {
            overrides.TryAdd(model.UpstreamModel, model);
        }

        foreach (var upstreamModel in overrides.Keys)
        {
            upstreamModels.Add(upstreamModel);
        }

        // 批量取回覆盖模型与全局模型的 plan/rules，避免每个上游模型各查一次。
        var overrideModels = overrides.Values.DistinctBy(model => model.Id).ToList();
        var plansByChannelModel = PlansByChannelModelId(
            overrideModels.Select(model => model.Id).ToList(),
            channel.Id);
        var channelRulesByPlan = RulesByPlanIds(plansByChannelModel.Values.Select(plan => plan.Id).ToList());

        var distinctUpstreamModels = upstreamModels
            .Where(model => Normalize(model).Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 每个上游模型还要做全局复合匹配(文档 C2 明确保留),匹配到的全局模型统一批量取 plan/rules。
        var globalMatches = new Dictionary<Guid, ModelInfo>();
        var perItemGlobalModel = ResolveGlobalModels(distinctUpstreamModels);
        foreach (var globalModel in perItemGlobalModel.Values)
        {
            if (globalModel is not null)
            {
                globalMatches.TryAdd(globalModel.Id, globalModel);
            }
        }

        var plansByGlobalModel = PlansByModelId(globalMatches.Keys.ToList());
        var globalRulesByPlan = RulesByPlanIds(plansByGlobalModel.Values.Select(plan => plan.Id).ToList());

        var items = distinctUpstreamModels
            .Select(upstreamModel =>
            {
                var globalModel = perItemGlobalModel[upstreamModel];
                overrides.TryGetValue(upstreamModel, out var overrideModel);
                return new ChannelModelInfoListItemResponse(
                    upstreamModel,
                    overrideModel is not null,
                    globalModel is null ? null : ToModelResponse(
                        globalModel,
                        providerById,
                        plansByGlobalModel.TryGetValue(globalModel.Id, out var plan) ? plan : null,
                        globalRulesByPlan),
                    overrideModel is null ? null : ToChannelModelResponse(
                        overrideModel,
                        providerById,
                        plansByChannelModel.TryGetValue(overrideModel.Id, out var channelPlan) ? channelPlan : null,
                        channelRulesByPlan));
            })
            .ToList();

        return ApiOpResult<ChannelModelInfoListResponse>.Succeed(
            new ChannelModelInfoListResponse(channel.Id, channel.Name, items));
    }

    public ApiOpResult<ChannelModelInfoResponsePayload> UpsertChannelModelInfo(
        Guid channelId,
        ChannelModelInfoUpsertRequest request)
    {
        try
        {
            ValidatePricingRequest(request.Pricing);
            var channel = FindChannelInScope(channelId);
            if (channel is null)
            {
                return ChannelModelValidationFailure("channel not found", 404);
            }

            var now = UnixTimeSeconds();
            var upstreamModel = NormalizeRequired(request.UpstreamModel, "upstream_model");
            var provider = ResolveProvider(request.ProviderId, request.ProviderCode);
            var modelKey = NormalizeRequired(request.ModelKey, "model_key");
            var existing = _channelModels.Table
                .Where(model => model.ChannelId == channel.Id)
                .AsEnumerable()
                .FirstOrDefault(model => string.Equals(
                    model.UpstreamModel,
                    upstreamModel,
                    StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                existing = new ChannelModelInfo
                {
                    Id = Guid.NewGuid(),
                    ChannelId = channel.Id,
                    CreatedAt = now
                };
                AssignChannelModel(existing, request, provider.Id, upstreamModel, modelKey, now);
                _channelModels.Insert(existing);
            }
            else
            {
                AssignChannelModel(existing, request, provider.Id, upstreamModel, modelKey, now);
                _channelModels.Update(existing);
            }

            ReplacePricing(existing, request.Pricing, ModelCatalogSources.Manual, now);
            BumpPricingVersion();

            return ApiOpResult<ChannelModelInfoResponsePayload>.Succeed(
                new ChannelModelInfoResponsePayload(ToChannelModelResponseForSingle(existing, ProviderMap())));
        }
        catch (ArgumentException exception)
        {
            return ChannelModelValidationFailure(exception.Message);
        }
    }

    public ApiOpResult DeleteChannelModelInfo(Guid channelId, Guid id)
    {
        var channel = FindChannelInScope(channelId);
        if (channel is null)
        {
            return ApiOpResult.Fail(404, "channel not found");
        }

        var model = _channelModels.Table.FirstOrDefault(item => item.ChannelId == channel.Id && item.Id == id);
        if (model is null)
        {
            return ApiOpResult.Fail(404, "channel model info not found");
        }

        RemovePlansForChannelModel(model.Id);
        _channelModels.Delete(model);
        BumpPricingVersion();
        return ApiOpResult.Succeed();
    }

    public bool SupportsImage(Guid? channelId, string? upstreamModel)
    {
        var actualModel = Normalize(upstreamModel);
        if (actualModel.Length == 0)
        {
            return false;
        }

        if (channelId.HasValue)
        {
            var channelModel = ResolveChannelModel(channelId.Value, actualModel);
            if (channelModel is not null)
            {
                var channelCapabilities = DeserializeObject(channelModel.CapabilitiesJson);
                if (channelCapabilities.ContainsKey("supports_image"))
                {
                    return ReadBoolean(channelCapabilities, "supports_image") == true;
                }
            }
        }

        var globalModel = ResolveGlobalModel(actualModel);
        return globalModel is not null && SupportsImage(globalModel.CapabilitiesJson);
    }

   public async Task<ModelPricingCalculationResult> CalculateCostAsync(
       Guid? channelId,
       string? requestModel,
       string? upstreamModel,
       ModelUsageVector usage,
       DateTimeOffset billingInstant)
   {
       // 缓存定价解析(按 channelId + upstreamModel),扁平 DTO 规避 PricingResolution 不可序列化的问题。
        // rules 与 provider code 随解析结果一起缓存(失效时机与 plan/provider 完全一致,
        // 见 BumpPricingVersion);usage 计算每请求不同,不可缓存。
        var cached = await ResolvePricingCachedAsync(channelId, upstreamModel);
        if (cached is null || !cached.HasModel || !cached.HasPlan)
        {
            return EmptyCalculation(cached?.Reason ?? "model_not_matched", billingInstant);
        }

        var planId = cached.PlanId!.Value;
        if (cached.Rules is null || cached.Rules.Count == 0)
        {
            return EmptyCalculation("pricing_plan_has_no_rules", billingInstant);
        }

        var rules = cached.Rules;
        var modelInfoId = cached.ModelInfoId;
        var channelModelInfoId = cached.ChannelModelInfoId;
        var modelKey = cached.ModelKey!;
        var matchType = cached.MatchType!;
        var matchPattern = cached.MatchPattern!;
        // 时段判定必须每请求现算:定价缓存 TTL 内可能跨越窗口边界,一旦缓存 phase 就会静默算错。
        // 同一请求只判定一次,四个计费项共用,避免 input 落峰段而 output 落谷段。
        var phase = PricingWindowCalendar.Evaluate(
            cached.TimeZoneId,
            cached.OffPeakWindowsJson,
            billingInstant);
        var total = 0m;
        var snapshotRules = new List<ModelPricingSnapshotRule>();
        foreach (var rule in rules)
        {
            var useOffPeak = phase.IsOffPeak && rule.OffPeakEnabled;
            var quantity = Quantity(rule, usage);
            var unitPrice = useOffPeak ? rule.OffPeakUnitPrice : rule.UnitPrice;
            var tiersJson = useOffPeak ? rule.OffPeakTiersJson : rule.TiersJson;
            var cost = CalculateRuleCost(rule.BillingMode, quantity, unitPrice, tiersJson, usage.InputTokens);
            total += cost;
            snapshotRules.Add(new ModelPricingSnapshotRule(
                rule.BillingItem,
                rule.BillingMode,
                quantity,
                unitPrice,
                cost,
                useOffPeak ? PricingPhases.OffPeak : PricingPhases.Peak));
        }

        var providerCode = cached.ProviderCode;
        var snapshot = new ModelPricingSnapshot(
            cached.Reason,
            cached.PlanCurrency ?? "USD",
            total,
            modelInfoId,
            channelModelInfoId,
            planId,
            providerCode,
            modelKey,
            matchType,
            matchPattern,
            phase.Phase,
            phase.Source,
            ToUnixSeconds(billingInstant),
            phase.TimeZoneId,
            phase.MatchedWindow,
            snapshotRules);
        var snapshotJson = JsonSerializer.Serialize(snapshot);

        return new ModelPricingCalculationResult(
            total,
            cached.PlanCurrency ?? "USD",
            modelInfoId,
            channelModelInfoId,
            planId,
            providerCode,
            modelKey,
            matchType,
            matchPattern,
            cached.Reason,
            phase.Phase,
            phase.Source,
            snapshotJson);
    }

    private async Task<CachedPricingResolution?> ResolvePricingCachedAsync(
        Guid? channelId,
        string? upstreamModel)
    {
        var versions = await GetPricingVersionsAsync();
        return await _cache.GetOrCreateAsync(
            CacheKeys.PricingContext(
                versions.RedisVersion,
                versions.LocalVersion,
                channelId,
                upstreamModel),
            () => Task.FromResult(ToCached(ResolvePricing(channelId, upstreamModel))),
            PricingCacheTtl);
    }

    private async Task<(int RedisVersion, int LocalVersion)> GetPricingVersionsAsync()
    {
        var redisVersion = Volatile.Read(ref _lastKnownRedisPricingVersion);
        if (_redis is not null && _redis.IsAvailable)
        {
            var db = _redis.GetDatabase();
            if (db is not null)
            {
                var pendingBump = Interlocked.Exchange(ref _pendingRedisPricingVersionBump, 0);
                try
                {
                    var observedRedisVersion = pendingBump > 0
                        ? checked((int)await db.StringIncrementAsync(PricingVersionKey))
                        : await ReadRedisPricingVersionAsync(db);
                    redisVersion = AdvanceLastKnownRedisPricingVersionTo(observedRedisVersion);
                }
                catch (RedisException)
                {
                    if (pendingBump > 0)
                    {
                        Interlocked.Exchange(ref _pendingRedisPricingVersionBump, 1);
                    }
                }
            }
        }

        return (redisVersion, Volatile.Read(ref _localPricingVersion));
    }

    private static async Task<int> ReadRedisPricingVersionAsync(IDatabase db)
    {
        var value = await db.StringGetAsync(PricingVersionKey);
        return value.HasValue ? (int)value : 0;
    }

    internal static readonly string PricingVersionKey = "pricing:version";

    internal static int BumpLocalPricingVersion()
    {
        return Interlocked.Increment(ref _localPricingVersion);
    }

    private static int AdvanceLastKnownRedisPricingVersionTo(int version)
    {
        var current = Volatile.Read(ref _lastKnownRedisPricingVersion);
        while (current < version)
        {
            var observed = Interlocked.CompareExchange(
                ref _lastKnownRedisPricingVersion,
                version,
                current);
            if (observed == current)
            {
                return version;
            }

            current = observed;
        }

        return current;
    }

    private void BumpPricingVersion()
    {
        BumpLocalPricingVersion();
        if (_redis is not null && _redis.IsAvailable)
        {
            var db = _redis.GetDatabase();
            if (db is not null)
            {
                Interlocked.Exchange(ref _pendingRedisPricingVersionBump, 0);
                try
                {
                    var redisVersion = checked((int)db.StringIncrement(PricingVersionKey));
                    AdvanceLastKnownRedisPricingVersionTo(redisVersion);
                    return;
                }
                catch (RedisException)
                {
                    // 本次变更已由进程内版本隔离，Redis 恢复后补一次全局失效。
                }
            }
        }

        if (_redis is not null)
        {
            Interlocked.Exchange(ref _pendingRedisPricingVersionBump, 1);
        }
    }

    private CachedPricingResolution ToCached(PricingResolution resolution)
    {
        var hasModel = resolution.HasModel;
        var providerCode = hasModel ? LoadProviderCode(resolution.ProviderId) : null;
        var rules = resolution.Plan is null
            ? null
            : LoadEnabledRules(resolution.Plan.Id);
        return new CachedPricingResolution(
            hasModel,
            resolution.Plan is not null,
            resolution.Plan?.Id,
            resolution.Plan?.Currency,
            resolution.Plan?.TimeZoneId ?? string.Empty,
            resolution.Plan?.OffPeakWindowsJson ?? "[]",
            hasModel ? resolution.ProviderId : null,
            providerCode,
            resolution.Model?.Id,
            resolution.ChannelModel?.Id,
            rules,
            hasModel ? resolution.ModelKey : null,
            hasModel ? resolution.MatchType : null,
            hasModel ? resolution.MatchPattern : null,
            resolution.Reason);
    }

    private string? LoadProviderCode(Guid providerId)
    {
        return _providers.TableNoTracking
            .Where(provider => provider.Id == providerId)
            .Select(provider => provider.Code)
            .FirstOrDefault();
    }

    private IReadOnlyList<CachedPricingRule>? LoadEnabledRules(Guid planId)
    {
        var rules = _rules.TableNoTracking
            .Where(rule => rule.PricingPlanId == planId && rule.Enabled)
            .Select(rule => new CachedPricingRule(
                rule.BillingItem,
                rule.BillingMode,
                rule.UnitPrice,
                rule.TiersJson,
                rule.OffPeakEnabled,
                rule.OffPeakUnitPrice,
                rule.OffPeakTiersJson))
            .ToList();
        return rules.Count == 0 ? null : rules;
    }

    private sealed record CachedPricingResolution(
        bool HasModel,
        bool HasPlan,
        Guid? PlanId,
        string? PlanCurrency,
        string TimeZoneId,
        string OffPeakWindowsJson,
        Guid? ProviderId,
        string? ProviderCode,
        Guid? ModelInfoId,
        Guid? ChannelModelInfoId,
        IReadOnlyList<CachedPricingRule>? Rules,
        string? ModelKey,
        string? MatchType,
        string? MatchPattern,
        string Reason);

    private sealed record CachedPricingRule(
        string BillingItem,
        string BillingMode,
        decimal UnitPrice,
        string TiersJson,
        bool OffPeakEnabled,
        decimal OffPeakUnitPrice,
        string OffPeakTiersJson);

    /// <summary>响应/查询所需的 provider 轻量投影，避免把完整 <see cref="ModelProvider"/> 实体拉进内存。</summary>
    private sealed record ProviderLookup(Guid Id, string Code, string Name);

    private PricingResolution ResolvePricing(
        Guid? channelId,
        string? upstreamModel)
    {
        var actualModel = Normalize(upstreamModel);
        if (actualModel.Length == 0)
        {
            return new PricingResolution("model_not_matched");
        }

        if (channelId.HasValue)
        {
            var channelModel = ResolveChannelModel(channelId.Value, actualModel);
            if (channelModel is not null)
            {
                var channelPlan = FindPlanForChannelModel(channelModel.Id, channelId.Value);
                if (channelPlan is not null)
                {
                    return new PricingResolution(
                        channelModel,
                        channelPlan,
                        "channel_model_override");
                }
            }
        }

        var globalModel = ResolveGlobalModel(actualModel);
        if (globalModel is not null)
        {
            return new PricingResolution(
                globalModel,
                FindPlanForModel(globalModel.Id),
                "global_model_match");
        }

        return new PricingResolution("model_not_matched");
    }

    private ChannelModelInfo? ResolveChannelModel(Guid channelId, string upstreamModel)
    {
        var normalized = Normalize(upstreamModel);
        if (normalized.Length == 0)
        {
            return null;
        }

        // 数据库精确相等快路径:常见情况下只取回匹配行,不拉取该渠道全部模型。
        // OrdinalIgnoreCase 与数据库 collation 语义不完全等价,未命中再退回内存比较兜底。
        var exact = _channelModels.TableNoTracking
            .Where(model => model.ChannelId == channelId
                && model.Enabled
                && model.UpstreamModel == normalized)
            .OrderByDescending(model => model.UpdatedAt)
            .FirstOrDefault();
        if (exact is not null)
        {
            return exact;
        }

        return _channelModels.TableNoTracking
            .Where(model => model.ChannelId == channelId && model.Enabled)
            .AsEnumerable()
            .FirstOrDefault(model => string.Equals(
                model.UpstreamModel,
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 批量解析多个上游模型的全局复合匹配。只加载一次 enabled 全局模型与
    /// provider 排序，保留与 <see cref="ResolveGlobalModel"/> 完全相同的
    /// MatchRank 打分与排序语义，避免列表接口随模型数量线性触发全表查询。
    /// </summary>
    private IReadOnlyDictionary<string, ModelInfo?> ResolveGlobalModels(
        IReadOnlyCollection<string> modelNames)
    {
        var distinctNames = modelNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctNames.Count == 0)
        {
            return new Dictionary<string, ModelInfo?>(StringComparer.OrdinalIgnoreCase);
        }

        // 空名统一跳过，避免对空白输入做无意义打分。
        var normalizedNames = distinctNames
            .Select(Normalize)
            .Where(name => name.Length > 0)
            .ToList();
        if (normalizedNames.Count == 0)
        {
            return new Dictionary<string, ModelInfo?>(StringComparer.OrdinalIgnoreCase);
        }

        var providerSort = _providers.TableNoTracking
            .Select(provider => new { provider.Id, provider.SortOrder })
            .ToDictionary(provider => provider.Id, provider => provider.SortOrder);
        var globalModels = _models.TableNoTracking
            .Where(model => model.Enabled && model.Scope == ModelInfoScopes.Global && model.ChannelId == null)
            .AsEnumerable()
            .Select(model => new
            {
                Model = model,
                ProviderSort = providerSort.TryGetValue(model.ProviderId, out var sort) ? sort : int.MaxValue
            })
            .ToList();

        var result = new Dictionary<string, ModelInfo?>(StringComparer.OrdinalIgnoreCase);
        foreach (var modelName in distinctNames)
        {
            var normalized = Normalize(modelName);
            if (normalized.Length == 0)
            {
                result[modelName] = null;
                continue;
            }

            var model = globalModels
                .Select(item => new
                {
                    item.Model,
                    item.ProviderSort,
                    Rank = MatchRank(item.Model.MatchType, item.Model.MatchPattern, normalized)
                })
                .Where(item => item.Rank is not null)
                .OrderBy(item => item.Rank!.Priority)
                .ThenByDescending(item => item.Rank!.PatternLength)
                .ThenBy(item => item.ProviderSort)
                .ThenBy(item => item.Model.ModelKey, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Model)
                .FirstOrDefault();
            result[modelName] = model;
        }

        return result;
    }

    private ModelInfo? ResolveGlobalModel(string modelName)
    {
        var normalized = Normalize(modelName);
        if (normalized.Length == 0)
        {
            return null;
        }

        return ResolveGlobalModels([normalized]).TryGetValue(normalized, out var model)
            ? model
            : null;
    }

    private ModelPricingPlan? FindPlanForModel(Guid modelInfoId)
    {
        return _plans.TableNoTracking
            .Where(plan => plan.ModelInfoId == modelInfoId
                && plan.ChannelModelInfoId == null
                && plan.ChannelId == null
                && plan.Enabled)
            .OrderByDescending(plan => plan.UpdatedAt)
            .FirstOrDefault();
    }

    private ModelPricingPlan? FindPlanForChannelModel(Guid channelModelInfoId, Guid channelId)
    {
        return _plans.TableNoTracking
            .Where(plan => plan.ChannelModelInfoId == channelModelInfoId
                && plan.ChannelId == channelId
                && plan.Enabled)
            .OrderByDescending(plan => plan.UpdatedAt)
            .FirstOrDefault();
    }

    private Dictionary<Guid, ModelPricingPlan> PlansByChannelModelIds(
        IReadOnlyCollection<Guid> channelModelIds)
    {
        if (channelModelIds.Count == 0)
        {
            return [];
        }

        var plans = _plans.TableNoTracking
            .Where(plan => plan.ChannelModelInfoId != null
                && plan.ChannelId != null
                && plan.Enabled
                && channelModelIds.Contains(plan.ChannelModelInfoId.Value))
            .ToList();
        return plans
            .GroupBy(plan => plan.ChannelModelInfoId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(plan => plan.UpdatedAt).First());
    }

    private static MatchScore? MatchRank(string matchType, string pattern, string modelName)
    {
        var normalizedPattern = Normalize(pattern);
        if (normalizedPattern.Length == 0)
        {
            return null;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        var matched = matchType switch
        {
            ModelMatchTypes.Exact => string.Equals(modelName, normalizedPattern, comparison),
            ModelMatchTypes.Prefix => modelName.StartsWith(normalizedPattern, comparison),
            ModelMatchTypes.Suffix => modelName.EndsWith(normalizedPattern, comparison),
            ModelMatchTypes.Contains => modelName.Contains(normalizedPattern, comparison),
            _ => false
        };
        if (!matched)
        {
            return null;
        }

        return new MatchScore(MatchPriority(matchType), normalizedPattern.Length);
    }

    private static int MatchPriority(string matchType)
    {
        return matchType switch
        {
            ModelMatchTypes.Exact => 0,
            ModelMatchTypes.Prefix => 1,
            ModelMatchTypes.Suffix => 2,
            ModelMatchTypes.Contains => 3,
            _ => 100
        };
    }

    private static int Quantity(ModelPricingRule rule, ModelUsageVector usage)
    {
        if (rule.BillingMode == ModelBillingModes.PerRequest)
        {
            return usage.RequestCount;
        }

        return rule.BillingItem switch
        {
            ModelBillingItems.Input => Math.Max(0, usage.InputTokens - usage.CacheWriteTokens - usage.CacheReadTokens),
            ModelBillingItems.Output => usage.OutputTokens,
            ModelBillingItems.CacheWrite => usage.CacheWriteTokens,
            ModelBillingItems.CacheRead => usage.CacheReadTokens,
            _ => 0
        };
    }

    private static int Quantity(CachedPricingRule rule, ModelUsageVector usage)
    {
        if (rule.BillingMode == ModelBillingModes.PerRequest)
        {
            return usage.RequestCount;
        }

        return rule.BillingItem switch
        {
            ModelBillingItems.Input => Math.Max(0, usage.InputTokens - usage.CacheWriteTokens - usage.CacheReadTokens),
            ModelBillingItems.Output => usage.OutputTokens,
            ModelBillingItems.CacheWrite => usage.CacheWriteTokens,
            ModelBillingItems.CacheRead => usage.CacheReadTokens,
            _ => 0
        };
    }

    private static decimal CalculateRuleCost(
        string billingMode,
        int quantity,
        decimal unitPrice,
        string tiersJson,
        int contextWindow)
    {
        if (quantity <= 0)
        {
            return 0m;
        }

        return billingMode switch
        {
            ModelBillingModes.PerRequest => quantity * unitPrice,
            ModelBillingModes.PerMillionTokens => quantity * unitPrice / 1_000_000m,
            ModelBillingModes.TieredTokens => CalculateContextWindowTierCost(quantity, contextWindow, tiersJson),
            _ => 0m
        };
    }

    // 阶梯 token 现按「上下文窗口档位」计费:用本次请求的输入长度(contextWindow=usage.InputTokens)
    // 选定档位,整段按该档单价计费,不再分段累乘。所有计费项共用 InputTokens 选档,各乘各 quantity。
    private static decimal CalculateContextWindowTierCost(int quantity, int contextWindow, string tiersJson)
    {
        var tiers = DeserializeTiers(tiersJson);
        if (tiers.Count == 0)
        {
            return 0m;
        }

        // 档位按 up_to 升序排列,取第一个 up_to >= contextWindow 的档;无上限档(up_to=null)兜底。
        var ordered = tiers.OrderBy(tier => tier.UpTo ?? long.MaxValue).ToList();
        var matched = ordered.FirstOrDefault(tier =>
            tier.UpTo.HasValue && tier.UpTo.Value >= contextWindow);
        matched ??= ordered.FirstOrDefault(tier => !tier.UpTo.HasValue);
        if (matched is null)
        {
            return 0m;
        }

        return quantity * matched.UnitPrice / 1_000_000m;
    }

    private static List<PricingTier> DeserializeTiers(string tiersJson)
    {
        if (string.IsNullOrWhiteSpace(tiersJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PricingTier>>(tiersJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void ReplacePricing(
        ModelInfo model,
        ModelPricingPlanRequest? request,
        string source,
        double now)
    {
        RemovePlans(model.Id, model.ChannelId);
        if (request is null)
        {
            return;
        }

        var plan = new ModelPricingPlan
        {
            ModelInfoId = model.Id,
            ChannelId = model.ChannelId,
            Currency = NormalizeCurrency(request.Currency),
            TimeZoneId = PricingWindowCalendar.NormalizeTimeZoneId(request.TimeZone),
            OffPeakWindowsJson = PricingWindowCalendar.Serialize(request.OffPeakWindows),
            Enabled = request.Enabled,
            Source = source,
            CreatedAt = now,
            UpdatedAt = now
        };
        _plans.Insert(plan);

        var rules = NormalizeRules(request.Rules)
            .Select(rule => ToPricingRule(plan.Id, rule))
            .ToList();
        if (rules.Count > 0)
        {
            _rules.Insert(rules);
        }
    }

    private void ReplacePricing(
        ChannelModelInfo model,
        ModelPricingPlanRequest? request,
        string source,
        double now)
    {
        RemovePlansForChannelModel(model.Id);
        if (request is null)
        {
            return;
        }

        var plan = new ModelPricingPlan
        {
            ModelInfoId = null,
            ChannelModelInfoId = model.Id,
            ChannelId = model.ChannelId,
            Currency = NormalizeCurrency(request.Currency),
            TimeZoneId = PricingWindowCalendar.NormalizeTimeZoneId(request.TimeZone),
            OffPeakWindowsJson = PricingWindowCalendar.Serialize(request.OffPeakWindows),
            Enabled = request.Enabled,
            Source = source,
            CreatedAt = now,
            UpdatedAt = now
        };
        _plans.Insert(plan);

        var rules = NormalizeRules(request.Rules)
            .Select(rule => ToPricingRule(plan.Id, rule))
            .ToList();
        if (rules.Count > 0)
        {
            _rules.Insert(rules);
        }
    }

    private void RemovePlans(Guid modelInfoId, Guid? channelId)
    {
        var plans = _plans.Table
            .Where(plan => plan.ModelInfoId == modelInfoId
                && plan.ChannelModelInfoId == null
                && plan.ChannelId == channelId)
            .ToList();
        RemovePlans(plans);
    }

    private void RemovePlansForChannelModel(Guid channelModelInfoId)
    {
        var plans = _plans.Table
            .Where(plan => plan.ChannelModelInfoId == channelModelInfoId)
            .ToList();
        RemovePlans(plans);
    }

    private void RemovePlans(IReadOnlyList<ModelPricingPlan> plans)
    {
        if (plans.Count == 0)
        {
            return;
        }

        var planIds = plans.Select(plan => plan.Id).ToList();
        var rules = _rules.Table
            .Where(rule => planIds.Contains(rule.PricingPlanId))
            .ToList();
        if (rules.Count > 0)
        {
            _rules.Delete(rules);
        }
        _plans.Delete(plans);
    }

    private void ReplaceImportedPricing(
        ModelInfo model,
        ModelCatalogPricingTransfer? pricing,
        double now,
        ModelCatalogImportOptions? options = null)
    {
        var opts = options ?? new ModelCatalogImportOptions { Source = ModelCatalogSources.Manual };
        if (pricing is null)
        {
            // KeepLocalPricingWhenRemoteNull: don't delete local pricing when remote is null.
            if (!opts.KeepLocalPricingWhenRemoteNull)
            {
                RemovePlans(model.Id, model.ChannelId);
            }
            return;
        }

        RemovePlans(model.Id, model.ChannelId);
        var plan = new ModelPricingPlan
        {
            ModelInfoId = model.Id,
            ChannelModelInfoId = null,
            ChannelId = null,
            Currency = NormalizeCurrency(pricing.Currency),
            TimeZoneId = PricingWindowCalendar.NormalizeTimeZoneId(pricing.TimeZone),
            OffPeakWindowsJson = PricingWindowCalendar.Serialize(pricing.OffPeakWindows),
            Enabled = pricing.Enabled,
            Source = opts.Source,
            CreatedAt = now,
            UpdatedAt = now
        };
        _plans.Insert(plan);

        var rules = pricing.Rules
            .Select(rule => ToImportedPricingRule(plan.Id, rule))
            .ToList();
        if (rules.Count > 0)
        {
            _rules.Insert(rules);
        }
    }

    private Channel? FindChannelInScope(Guid channelId)
    {
        if (channelId == Guid.Empty)
        {
            return null;
        }

        var currentUser = _workContext.RequireUser();
        var channel = _channels.TableNoTracking.FirstOrDefault(item => item.Id == channelId);
        if (channel is null)
        {
            return null;
        }

        return currentUser.Role == "superadmin" || channel.OwnerUserId == currentUser.UserId
            ? channel
            : null;
    }

    private HashSet<string> ListChannelUpstreamModels(Channel channel)
    {
        var upstreamModels = _mappings.TableNoTracking
            .Where(mapping => mapping.ChannelId == channel.Id && mapping.Enabled)
            .OrderBy(mapping => mapping.Position)
            .Select(mapping => mapping.UpstreamModel)
            .ToList()
            .Where(model => Normalize(model).Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (upstreamModels.Count > 0)
        {
            return upstreamModels;
        }

        foreach (var item in DeserializeList(channel.ModelsJson))
        {
            if (item is not IReadOnlyDictionary<string, object?> mapping)
            {
                continue;
            }

            var requestModel = JsonDictionaryValue.String(mapping, "model");
            var upstreamModel = JsonDictionaryValue.String(mapping, "upstream_model");
            if (upstreamModel.Length == 0)
            {
                upstreamModel = requestModel;
            }

            if (upstreamModel.Length > 0)
            {
                upstreamModels.Add(upstreamModel);
            }
        }

        return upstreamModels;
    }

    private static void AssignChannelModel(
        ChannelModelInfo model,
        ChannelModelInfoUpsertRequest request,
        Guid providerId,
        string upstreamModel,
        string modelKey,
        double now)
    {
        model.UpstreamModel = upstreamModel;
        model.ProviderId = providerId;
        model.ModelKey = modelKey;
        model.DisplayName = DisplayName(request.DisplayName, modelKey);
        model.Description = Normalize(request.Description);
        model.MatchType = NormalizeMatchType(request.MatchType);
        model.MatchPattern = NormalizeMatchPattern(request.MatchPattern, modelKey);
        model.CatalogJson = SerializeObject(request.Catalog);
        model.CapabilitiesJson = SerializeObject(request.Capabilities);
        model.Enabled = request.Enabled;
        model.Source = ModelCatalogSources.Manual;
        model.UpdatedAt = now;
    }

    private ChannelModelInfoResponse ToChannelModelResponse(
        ChannelModelInfo model,
        IReadOnlyDictionary<Guid, ProviderLookup> providerById,
        ModelPricingPlan? plan,
        IReadOnlyDictionary<Guid, List<ModelPricingRule>> rulesByPlan)
    {
        providerById.TryGetValue(model.ProviderId, out var provider);

        return new ChannelModelInfoResponse(
            model.Id,
            model.ChannelId,
            model.UpstreamModel,
            model.ProviderId,
            provider?.Code ?? string.Empty,
            provider?.Name ?? string.Empty,
            model.ModelKey,
            model.DisplayName,
            model.Description,
            model.MatchType,
            model.MatchPattern,
            DeserializeObject(model.CatalogJson),
            DeserializeObject(model.CapabilitiesJson),
            model.Enabled,
            model.Source,
            plan is null ? null : ToPlanResponse(plan, rulesByPlan),
            model.CreatedAt,
            model.UpdatedAt);
    }

    private ModelProvider ResolveProvider(Guid? providerId, string? providerCode)
    {
        if (providerId.HasValue)
        {
            var provider = _providers.TableNoTracking.FirstOrDefault(item => item.Id == providerId.Value);
            if (provider is not null)
            {
                return provider;
            }
        }

        var normalizedCode = NormalizeRequired(providerCode, "provider_code").ToLowerInvariant();
        return _providers.TableNoTracking.FirstOrDefault(provider => provider.Code == normalizedCode)
            ?? throw new ArgumentException("provider_code is invalid", nameof(providerCode));
    }

    private bool ModelExists(string scope, Guid? channelId, string modelKey, Guid? excludeId)
    {
        return _models.TableNoTracking.Any(model =>
            model.Scope == scope
            && model.ChannelId == channelId
            && model.ModelKey == modelKey
            && (!excludeId.HasValue || model.Id != excludeId.Value));
    }

    private Dictionary<Guid, ProviderLookup> ProviderMap()
    {
        return _providers.TableNoTracking
            .Select(provider => new ProviderLookup(provider.Id, provider.Code, provider.Name))
            .ToDictionary(provider => provider.Id);
    }

    private int NextProviderSortOrder()
    {
        var currentMax = _providers.TableNoTracking
            .Select(provider => (int?)provider.SortOrder)
            .Max() ?? 0;
        return currentMax + 10;
    }

    private ModelInfoResponse ToModelResponse(
        ModelInfo model,
        IReadOnlyDictionary<Guid, ProviderLookup> providerById,
        ModelPricingPlan? plan,
        IReadOnlyDictionary<Guid, List<ModelPricingRule>> rulesByPlan)
    {
        providerById.TryGetValue(model.ProviderId, out var provider);

        return new ModelInfoResponse(
            model.Id,
            model.Scope,
            model.ProviderId,
            provider?.Code ?? string.Empty,
            provider?.Name ?? string.Empty,
            model.ChannelId,
            model.ModelKey,
            model.DisplayName,
            model.Description,
            model.MatchType,
            model.MatchPattern,
            DeserializeObject(model.CatalogJson),
            DeserializeObject(model.CapabilitiesJson),
            model.Enabled,
            model.Source,
            plan is null ? null : ToPlanResponse(plan, rulesByPlan),
            model.CreatedAt,
            model.UpdatedAt);
    }

    private static ModelPricingPlanResponse ToPlanResponse(
        ModelPricingPlan plan,
        IReadOnlyDictionary<Guid, List<ModelPricingRule>> rulesByPlan)
    {
        var rules = (rulesByPlan.TryGetValue(plan.Id, out var raw)
                ? raw
                : [])
            .OrderBy(rule => rule.BillingItem)
            .Select(rule => new ModelPricingRuleResponse(
                rule.Id,
                rule.BillingItem,
                rule.BillingMode,
                rule.UnitPrice,
                DeserializeList(rule.TiersJson),
                rule.OffPeakEnabled,
                rule.OffPeakUnitPrice,
                DeserializeList(rule.OffPeakTiersJson),
                rule.Enabled))
            .ToList();

        return new ModelPricingPlanResponse(
            plan.Id,
            plan.ModelInfoId,
            plan.ChannelModelInfoId,
            plan.ChannelId,
            plan.Currency,
            plan.TimeZoneId,
            PricingWindowCalendar.Deserialize(plan.OffPeakWindowsJson),
            plan.Enabled,
            plan.Source,
            rules,
            plan.CreatedAt,
            plan.UpdatedAt);
    }

    private ModelInfoResponse ToModelResponseForSingle(
        ModelInfo model,
        IReadOnlyDictionary<Guid, ProviderLookup> providerById)
    {
        var plansByModel = PlansByModelId([model.Id]);
        var rulesByPlan = RulesByPlanIds(plansByModel.Values.Select(plan => plan.Id).ToList());
        return ToModelResponse(
            model,
            providerById,
            plansByModel.TryGetValue(model.Id, out var plan) ? plan : null,
            rulesByPlan);
    }

    private ChannelModelInfoResponse ToChannelModelResponseForSingle(
        ChannelModelInfo model,
        IReadOnlyDictionary<Guid, ProviderLookup> providerById)
    {
        var plansByModel = PlansByChannelModelId([model.Id], model.ChannelId);
        var rulesByPlan = RulesByPlanIds(plansByModel.Values.Select(plan => plan.Id).ToList());
        return ToChannelModelResponse(
            model,
            providerById,
            plansByModel.TryGetValue(model.Id, out var plan) ? plan : null,
            rulesByPlan);
    }

    /// <summary>按 ModelInfoId 批量取回全局模型的最新启用计划，并按 Id 建索引。</summary>
    private Dictionary<Guid, ModelPricingPlan> PlansByModelId(IReadOnlyCollection<Guid> modelIds)
    {
        if (modelIds.Count == 0)
        {
            return [];
        }

        var plans = new List<ModelPricingPlan>();
        foreach (var page in Pages(modelIds))
        {
            plans.AddRange(_plans.TableNoTracking
                .Where(plan => plan.ModelInfoId != null
                    && plan.ChannelModelInfoId == null
                    && plan.ChannelId == null
                    && plan.Enabled
                    && page.Contains(plan.ModelInfoId!.Value))
                .ToList());
        }

        return plans
            .GroupBy(plan => plan.ModelInfoId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(plan => plan.UpdatedAt).First());
    }

    /// <summary>按 ChannelModelInfoId 批量取回某渠道的最新启用计划，并按 Id 建索引。</summary>
    private Dictionary<Guid, ModelPricingPlan> PlansByChannelModelId(
        IReadOnlyCollection<Guid> channelModelIds,
        Guid channelId)
    {
        if (channelModelIds.Count == 0)
        {
            return [];
        }

        var plans = new List<ModelPricingPlan>();
        foreach (var page in Pages(channelModelIds))
        {
            plans.AddRange(_plans.TableNoTracking
                .Where(plan => plan.ChannelModelInfoId != null
                    && plan.ChannelId == channelId
                    && plan.Enabled
                    && page.Contains(plan.ChannelModelInfoId!.Value))
                .ToList());
        }

        return plans
            .GroupBy(plan => plan.ChannelModelInfoId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(plan => plan.UpdatedAt).First());
    }

    /// <summary>按 PricingPlanId 批量取回全部规则，并按 plan Id 分组。</summary>
    private Dictionary<Guid, List<ModelPricingRule>> RulesByPlanIds(IReadOnlyCollection<Guid> planIds)
    {
        if (planIds.Count == 0)
        {
            return [];
        }

        var rules = new List<ModelPricingRule>();
        foreach (var page in Pages(planIds))
        {
            rules.AddRange(_rules.TableNoTracking
                .Where(rule => page.Contains(rule.PricingPlanId))
                .ToList());
        }

        return rules
            .GroupBy(rule => rule.PricingPlanId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    /// <summary>把 Guid 集合切成页，每页最多 900 个，规避 SQLite 单查询 999 参数上限。</summary>
    private static IEnumerable<IReadOnlyCollection<Guid>> Pages(IReadOnlyCollection<Guid> ids)
    {
        const int pageSize = 900;
        var list = ids as List<Guid> ?? ids.ToList();
        for (var offset = 0; offset < list.Count; offset += pageSize)
        {
            yield return list.GetRange(offset, Math.Min(pageSize, list.Count - offset));
        }
    }

    private static ModelProviderResponse ToProviderResponse(ModelProvider provider)
    {
        return new ModelProviderResponse(
            provider.Id,
            provider.Code,
            provider.Name,
            provider.Enabled,
            provider.SortOrder,
            provider.Source,
            provider.CreatedAt,
            provider.UpdatedAt);
    }

    private static ModelCatalogProviderTransfer ToProviderTransfer(ModelProvider provider)
    {
        return new ModelCatalogProviderTransfer
        {
            Code = provider.Code,
            Name = provider.Name,
            Enabled = provider.Enabled,
            SortOrder = provider.SortOrder
        };
    }

    private static ModelCatalogModelTransfer ToModelTransfer(
        ModelInfo model,
        string providerCode,
        ModelPricingPlan? plan,
        Func<ModelPricingPlan, IReadOnlyList<ModelPricingRule>> rulesForPlan)
    {
        return new ModelCatalogModelTransfer
        {
            ProviderCode = providerCode,
            ModelKey = model.ModelKey,
            DisplayName = model.DisplayName,
            Description = model.Description,
            MatchType = model.MatchType,
            MatchPattern = model.MatchPattern,
            Catalog = DeserializeObject(model.CatalogJson),
            Capabilities = DeserializeObject(model.CapabilitiesJson),
            Enabled = model.Enabled,
            Pricing = plan is null
                ? null
                : new ModelCatalogPricingTransfer
                {
                    Currency = plan.Currency,
                    TimeZone = plan.TimeZoneId,
                    OffPeakWindows = PricingWindowCalendar.Deserialize(plan.OffPeakWindowsJson).ToList(),
                    Enabled = plan.Enabled,
                    Rules = rulesForPlan(plan).Select(ToPricingRuleTransfer).ToList()
                }
        };
    }

    private static ModelCatalogPricingRuleTransfer ToPricingRuleTransfer(ModelPricingRule rule)
    {
        return new ModelCatalogPricingRuleTransfer
        {
            BillingItem = rule.BillingItem,
            BillingMode = rule.BillingMode,
            UnitPrice = rule.UnitPrice,
            Tiers = DeserializeTiers(rule.TiersJson)
                .Select(tier => new ModelCatalogPricingTierTransfer
                {
                    UpTo = tier.UpTo,
                    UnitPrice = tier.UnitPrice
                })
                .ToList(),
            OffPeakEnabled = rule.OffPeakEnabled,
            OffPeakUnitPrice = rule.OffPeakUnitPrice,
            OffPeakTiers = DeserializeTiers(rule.OffPeakTiersJson)
                .Select(tier => new ModelCatalogPricingTierTransfer
                {
                    UpTo = tier.UpTo,
                    UnitPrice = tier.UnitPrice
                })
                .ToList(),
            Enabled = rule.Enabled
        };
    }

    private static IReadOnlyList<ModelPricingRuleRequest> NormalizeRules(IEnumerable<ModelPricingRuleRequest>? rules)
    {
        var normalized = (rules ?? []).ToList();
        if (normalized.Count > 0)
        {
            return normalized;
        }

        return
        [
            new ModelPricingRuleRequest { BillingItem = ModelBillingItems.Input, BillingMode = ModelBillingModes.PerMillionTokens },
            new ModelPricingRuleRequest { BillingItem = ModelBillingItems.Output, BillingMode = ModelBillingModes.PerMillionTokens },
            new ModelPricingRuleRequest { BillingItem = ModelBillingItems.CacheWrite, BillingMode = ModelBillingModes.PerMillionTokens },
            new ModelPricingRuleRequest { BillingItem = ModelBillingItems.CacheRead, BillingMode = ModelBillingModes.PerMillionTokens }
        ];
    }

    private static ModelPricingRule ToPricingRule(Guid planId, ModelPricingRuleRequest rule)
    {
        var billingMode = NormalizeBillingMode(rule.BillingMode);
        ValidateTierRules(billingMode, rule.Tiers, rule.OffPeakEnabled, rule.OffPeakTiers);
        return new ModelPricingRule
        {
            PricingPlanId = planId,
            BillingItem = NormalizeBillingItem(rule.BillingItem),
            BillingMode = billingMode,
            UnitPrice = ValidatePrice(rule.UnitPrice, "unit_price"),
            TiersJson = SerializeTiers(rule.Tiers),
            OffPeakEnabled = rule.OffPeakEnabled,
            OffPeakUnitPrice = ValidatePrice(rule.OffPeakUnitPrice, "off_peak_unit_price"),
            OffPeakTiersJson = SerializeTiers(rule.OffPeakTiers),
            Enabled = rule.Enabled
        };
    }

    // 价格计划的校验必须在写库之前跑完:仓储的 Insert 会立即 SaveChanges,
    // 校验失败若发生在插入之后,会留下一个没有价格的模型,该模型后续请求会静默按 0 成本记账。
    private static void ValidatePricingRequest(ModelPricingPlanRequest? request)
    {
        if (request is null)
        {
            return;
        }

        NormalizeCurrency(request.Currency);
        PricingWindowCalendar.NormalizeTimeZoneId(request.TimeZone);
        PricingWindowCalendar.Normalize(request.OffPeakWindows);
        foreach (var rule in NormalizeRules(request.Rules))
        {
            ToPricingRule(Guid.Empty, rule);
        }
    }

    private static ModelPricingRule ToImportedPricingRule(Guid planId, ModelCatalogPricingRuleTransfer rule)
    {
        var billingMode = NormalizeBillingMode(rule.BillingMode);
        ValidateTierRules(billingMode, rule.Tiers, rule.OffPeakEnabled, rule.OffPeakTiers);
        return new ModelPricingRule
        {
            PricingPlanId = planId,
            BillingItem = NormalizeBillingItem(rule.BillingItem),
            BillingMode = billingMode,
            UnitPrice = ValidatePrice(rule.UnitPrice, "unit_price"),
            TiersJson = SerializeImportTiers(rule.Tiers),
            OffPeakEnabled = rule.OffPeakEnabled,
            OffPeakUnitPrice = ValidatePrice(rule.OffPeakUnitPrice, "off_peak_unit_price"),
            OffPeakTiersJson = SerializeImportTiers(rule.OffPeakTiers),
            Enabled = rule.Enabled
        };
    }

    // 阶梯计价校验:峰/谷档位必须至少一档、至少保留一个无上限兜底档(up_to=null),
    // 且最多一个兜底档,否则窗口可能落在任何档位之外而静默按 0 计费。
    private static void ValidateTierRules(
        string billingMode,
        IEnumerable<ModelPricingTierRequest>? tiers,
        bool offPeakEnabled,
        IEnumerable<ModelPricingTierRequest>? offPeakTiers)
    {
        if (billingMode != ModelBillingModes.TieredTokens)
        {
            return;
        }

        ValidateTierList(
            tiers?.Select(tier => new PricingTier { UpTo = tier.UpTo, UnitPrice = tier.UnitPrice }),
            "tiers");
        if (offPeakEnabled)
        {
            ValidateTierList(
                offPeakTiers?.Select(tier => new PricingTier { UpTo = tier.UpTo, UnitPrice = tier.UnitPrice }),
                "off_peak_tiers");
        }
    }

    private static void ValidateTierRules(
        string billingMode,
        IEnumerable<ModelCatalogPricingTierTransfer>? tiers,
        bool offPeakEnabled,
        IEnumerable<ModelCatalogPricingTierTransfer>? offPeakTiers)
    {
        if (billingMode != ModelBillingModes.TieredTokens)
        {
            return;
        }

        ValidateTierList(
            tiers?.Select(tier => new PricingTier { UpTo = tier.UpTo, UnitPrice = tier.UnitPrice }),
            "tiers");
        if (offPeakEnabled)
        {
            ValidateTierList(
                offPeakTiers?.Select(tier => new PricingTier { UpTo = tier.UpTo, UnitPrice = tier.UnitPrice }),
                "off_peak_tiers");
        }
    }

    private static void ValidateTierList(IEnumerable<PricingTier>? tiers, string fieldName)
    {
        var list = tiers?.ToList() ?? [];
        if (list.Count == 0)
        {
            throw new ArgumentException(
                $"{fieldName} is required when billing_mode is tiered_tokens",
                fieldName);
        }

        var openEnded = list.Count(tier => tier.UpTo is null);
        if (openEnded > 1)
        {
            throw new ArgumentException(
                $"{fieldName} must have at most one unlimited tier (up_to: null)",
                fieldName);
        }

        if (list.Any(tier => tier.UpTo is not null && tier.UpTo <= 0))
        {
            throw new ArgumentException(
                $"{fieldName} up_to must be positive when set",
                fieldName);
        }
    }

    private static string NormalizeMatchType(string? value)
    {
        var normalized = Normalize(value).ToLowerInvariant();
        return normalized switch
        {
            "" => ModelMatchTypes.Exact,
            ModelMatchTypes.Exact => normalized,
            ModelMatchTypes.Prefix => normalized,
            ModelMatchTypes.Suffix => normalized,
            ModelMatchTypes.Contains => normalized,
            _ => throw new ArgumentException("match_type is invalid", nameof(value))
        };
    }

    private static string NormalizeBillingItem(string? value)
    {
        var normalized = Normalize(value).ToLowerInvariant();
        return normalized switch
        {
            ModelBillingItems.Input => normalized,
            ModelBillingItems.Output => normalized,
            ModelBillingItems.CacheWrite => normalized,
            ModelBillingItems.CacheRead => normalized,
            _ => throw new ArgumentException("billing_item is invalid", nameof(value))
        };
    }

    private static string NormalizeBillingMode(string? value)
    {
        var normalized = Normalize(value).ToLowerInvariant();
        return normalized switch
        {
            "" => ModelBillingModes.PerMillionTokens,
            ModelBillingModes.PerRequest => normalized,
            ModelBillingModes.PerMillionTokens => normalized,
            ModelBillingModes.TieredTokens => normalized,
            _ => throw new ArgumentException("billing_mode is invalid", nameof(value))
        };
    }

    private static string NormalizeCurrency(string? value)
    {
        var normalized = Normalize(value).ToUpperInvariant();
        return normalized.Length == 0 ? "USD" : normalized;
    }

    private static string NormalizeMatchPattern(string? matchPattern, string modelKey)
    {
        var normalized = Normalize(matchPattern);
        return normalized.Length == 0 ? NormalizeRequired(modelKey, "model_key") : normalized;
    }

    private static string NormalizeProviderCode(string? value)
    {
        var normalized = Normalize(value).ToLowerInvariant();
        return normalized;
    }

    private static string NormalizeProviderCodeRequired(string? value)
    {
        var normalized = NormalizeProviderCode(value);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("provider_code is required", nameof(value));
        }

        if (!normalized.All(IsProviderCodeCharacter))
        {
            throw new ArgumentException("provider_code may only contain lowercase letters, numbers, dots, underscores, and hyphens", nameof(value));
        }

        return normalized;
    }

    private static bool IsProviderCodeCharacter(char value)
    {
        return (value >= 'a' && value <= 'z')
            || (value >= '0' && value <= '9')
            || value == '.'
            || value == '_'
            || value == '-';
    }

    private static string DisplayName(string? value, string modelKey)
    {
        var normalized = Normalize(value);
        return normalized.Length == 0 ? modelKey : normalized;
    }

    private static decimal ValidatePrice(decimal value, string field)
    {
        if (value < 0)
        {
            throw new ArgumentException($"{field} must be a non-negative number", field);
        }

        return value;
    }

    private static string NormalizeRequired(string? value, string field)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{field} is required", field);
        }

        return normalized;
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string SerializeObject(IReadOnlyDictionary<string, object?>? value)
    {
        return JsonSerializer.Serialize(JsonRequestValue.Object(value));
    }

    private static string SerializeTiers(IEnumerable<ModelPricingTierRequest>? tiers)
    {
        return JsonSerializer.Serialize((tiers ?? []).Select(tier => new PricingTier
        {
            UpTo = tier.UpTo,
            UnitPrice = ValidatePrice(tier.UnitPrice, "unit_price")
        }).ToList());
    }

    private static string SerializeImportTiers(IEnumerable<ModelCatalogPricingTierTransfer>? tiers)
    {
        return JsonSerializer.Serialize((tiers ?? []).Select(tier => new PricingTier
        {
            UpTo = tier.UpTo,
            UnitPrice = ValidatePrice(tier.UnitPrice, "unit_price")
        }).ToList());
    }

    private static Dictionary<string, object?> DeserializeObject(string? raw)
    {
        return DeserializeJson(raw) as Dictionary<string, object?>
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static List<object?> DeserializeList(string? raw)
    {
        return DeserializeJson(raw) as List<object?> ?? [];
    }

    private static object? DeserializeJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return FromJsonElement(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SupportsImage(string capabilitiesJson)
    {
        return DeserializeObject(capabilitiesJson).TryGetValue("supports_image", out var value)
            && value is true;
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

    private static string ProviderText(
        IReadOnlyDictionary<Guid, ProviderLookup> providerById,
        Guid providerId)
    {
        return providerById.TryGetValue(providerId, out var provider)
            ? $"{provider.Code} {provider.Name}"
            : string.Empty;
    }

    private static ApiOpResult<ModelInfoResponsePayload> ModelValidationFailure(string message)
    {
        return ApiOpResult<ModelInfoResponsePayload>.Fail(400, message);
    }

    private static ApiOpResult<ChannelModelInfoResponsePayload> ChannelModelValidationFailure(
        string message,
        int statusCode = 400)
    {
        return ApiOpResult<ChannelModelInfoResponsePayload>.Fail(statusCode, message);
    }

    private static ApiOpResult<ModelProviderResponsePayload> ProviderValidationFailure(string message)
    {
        return ApiOpResult<ModelProviderResponsePayload>.Fail(400, message);
    }

    private static ApiOpResult<ModelCatalogImportResult> ImportFailure(
        string description,
        IReadOnlyList<string>? errors = null)
    {
        var details = errors ?? [description];
        return ApiOpResult<ModelCatalogImportResult>.Fail(
            400,
            description,
            new ModelCatalogImportResult
            {
                DryRun = true,
                ErrorCount = details.Count,
                Errors = details
            });
    }

    private static List<string> ValidateImportDocument(ModelCatalogTransferDocument document)
    {
        var errors = new List<string>();
        if (!string.Equals(document.Type, "model_catalog", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("type must be 'model_catalog'");
        }

        // v1 没有峰谷字段,导入时按未启用处理;v2 起带 time_zone 与 off_peak_windows。
        if (document.Version is not (1 or ModelCatalogDocumentVersion))
        {
            errors.Add($"version must be 1 or {ModelCatalogDocumentVersion}");
        }

        var providerCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in document.Providers)
        {
            try
            {
                var code = NormalizeProviderCodeRequired(provider.Code);
                if (!providerCodes.Add(code))
                {
                    errors.Add($"provider_code '{code}' is duplicated");
                }

                var name = Normalize(provider.Name);
                if (name.Length == 0)
                {
                    errors.Add($"provider '{code}' name is required");
                }
            }
            catch (ArgumentException exception)
            {
                errors.Add(exception.Message);
            }
        }

        var modelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in document.Models)
        {
            try
            {
                var modelKey = NormalizeRequired(model.ModelKey, "model_key");
                if (!modelKeys.Add(modelKey))
                {
                    errors.Add($"model_key '{modelKey}' is duplicated");
                }

                ValidateImportModel(model);
            }
            catch (ArgumentException exception)
            {
                errors.Add(exception.Message);
            }
        }

        return errors;
    }

    private static void ValidateImportModel(ModelCatalogModelTransfer model)
    {
        NormalizeProviderCodeRequired(model.ProviderCode);
        NormalizeRequired(model.ModelKey, "model_key");
        NormalizeMatchType(model.MatchType);
        NormalizeMatchPattern(model.MatchPattern, model.ModelKey);
        if (model.Pricing is null)
        {
            return;
        }

        NormalizeCurrency(model.Pricing.Currency);
        PricingWindowCalendar.NormalizeTimeZoneId(model.Pricing.TimeZone);
        PricingWindowCalendar.Normalize(model.Pricing.OffPeakWindows);
        foreach (var rule in model.Pricing.Rules)
        {
            NormalizeBillingItem(rule.BillingItem);
            var billingMode = NormalizeBillingMode(rule.BillingMode);
            ValidatePrice(rule.UnitPrice, "unit_price");
            ValidatePrice(rule.OffPeakUnitPrice, "off_peak_unit_price");
            ValidateTierRules(billingMode, rule.Tiers, rule.OffPeakEnabled, rule.OffPeakTiers);
            foreach (var tier in rule.Tiers.Concat(rule.OffPeakTiers))
            {
                if (tier.UpTo.HasValue && tier.UpTo.Value < 0)
                {
                    throw new ArgumentException("up_to must be a non-negative number", nameof(tier.UpTo));
                }

                ValidatePrice(tier.UnitPrice, "unit_price");
            }
        }
    }

    private static bool ProviderUnchanged(ModelProvider provider, ModelCatalogProviderTransfer transfer)
    {
        return provider.Name == DisplayName(transfer.Name, provider.Code)
            && provider.Enabled == transfer.Enabled
            && provider.SortOrder == transfer.SortOrder;
    }

    private static bool ModelUnchanged(
        ModelInfo model,
        Guid providerId,
        ModelCatalogModelTransfer transfer,
        IReadOnlyDictionary<Guid, ModelPricingPlan> plansByModelId,
        IReadOnlyDictionary<Guid, List<ModelPricingRule>> rulesByPlanId)
    {
        if (model.ProviderId != providerId
            || model.DisplayName != DisplayName(transfer.DisplayName, model.ModelKey)
            || model.Description != Normalize(transfer.Description)
            || model.MatchType != NormalizeMatchType(transfer.MatchType)
            || model.MatchPattern != NormalizeMatchPattern(transfer.MatchPattern, model.ModelKey)
            || model.CatalogJson != SerializeObject(JsonRequestValue.Object(transfer.Catalog))
            || model.CapabilitiesJson != SerializeObject(JsonRequestValue.Object(transfer.Capabilities))
            || model.Enabled != transfer.Enabled)
        {
            return false;
        }

        if (!plansByModelId.TryGetValue(model.Id, out var plan))
        {
            return transfer.Pricing is null;
        }

        if (transfer.Pricing is null)
        {
            return false;
        }

        return PricingUnchanged(model, plan, transfer.Pricing, rulesByPlanId);
    }

    private static bool PricingUnchanged(
        ModelInfo model,
        ModelPricingPlan plan,
        ModelCatalogPricingTransfer transfer,
        IReadOnlyDictionary<Guid, List<ModelPricingRule>> rulesByPlanId)
    {
        if (plan.Currency != NormalizeCurrency(transfer.Currency)
            || plan.TimeZoneId != PricingWindowCalendar.NormalizeTimeZoneId(transfer.TimeZone)
            || plan.OffPeakWindowsJson != PricingWindowCalendar.Serialize(transfer.OffPeakWindows)
            || plan.Enabled != transfer.Enabled
            || plan.ModelInfoId != model.Id
            || plan.ChannelModelInfoId != null
            || plan.ChannelId != null)
        {
            return false;
        }

        if (!rulesByPlanId.TryGetValue(plan.Id, out var rules))
        {
            return true;
        }

        if (rules.Count != transfer.Rules.Count)
        {
            return false;
        }

        var expected = transfer.Rules
            .Select(rule => new ModelPricingRule
            {
                BillingItem = NormalizeBillingItem(rule.BillingItem),
                BillingMode = NormalizeBillingMode(rule.BillingMode),
                UnitPrice = rule.UnitPrice,
                TiersJson = SerializeImportTiers(rule.Tiers),
                OffPeakEnabled = rule.OffPeakEnabled,
                OffPeakUnitPrice = rule.OffPeakUnitPrice,
                OffPeakTiersJson = SerializeImportTiers(rule.OffPeakTiers),
                Enabled = rule.Enabled
            })
            .ToList();
        return rules
            .Select(rule => (
                NormalizeBillingItem(rule.BillingItem),
                NormalizeBillingMode(rule.BillingMode),
                rule.UnitPrice,
                rule.TiersJson,
                rule.OffPeakEnabled,
                rule.OffPeakUnitPrice,
                rule.OffPeakTiersJson,
                rule.Enabled))
            .SequenceEqual(expected.Select(rule =>
                (rule.BillingItem,
                    rule.BillingMode,
                    rule.UnitPrice,
                    rule.TiersJson,
                    rule.OffPeakEnabled,
                    rule.OffPeakUnitPrice,
                    rule.OffPeakTiersJson,
                    rule.Enabled)));
    }

    private static ModelCatalogImportCounts ImportCounts(
        IEnumerable<bool> created,
        IEnumerable<bool> unchanged)
    {
        var createdList = created.ToList();
        var unchangedList = unchanged.ToList();
        return new ModelCatalogImportCounts
        {
            Created = createdList.Count(value => value),
            Updated = createdList.Count - createdList.Count(value => value) - unchangedList.Count(value => value),
            Unchanged = unchangedList.Count(value => value)
        };
    }

    private static IOpenCodexDbContext? SharedContext(
        IRepository<ModelProvider> providers,
        IRepository<ModelInfo> models,
        IRepository<ChannelModelInfo> channelModels,
        IRepository<ModelPricingPlan> plans,
        IRepository<ModelPricingRule> rules,
        IRepository<ChannelModelMapping> mappings,
        IRepository<Channel> channels)
    {
        var repositories = new object[] { providers, models, channelModels, plans, rules, mappings, channels };
        return repositories
            .Select(repository => (Repository: repository, Property: repository.GetType().GetProperty(
                "SharedContext",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)))
            .Select(item => item.Property?.GetValue(item.Repository))
            .OfType<IOpenCodexDbContext>()
            .Distinct()
            .SingleOrDefault();
    }

    private static ModelPricingCalculationResult EmptyCalculation(
        string resolution,
        DateTimeOffset billingInstant)
    {
        var snapshot = new ModelPricingSnapshot(
            resolution,
            "USD",
            0m,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            PricingPhases.Peak,
            PricingPhaseSources.Disabled,
            ToUnixSeconds(billingInstant),
            string.Empty,
            null,
            []);
        return new ModelPricingCalculationResult(
            0m,
            "USD",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            resolution,
            PricingPhases.Peak,
            PricingPhaseSources.Disabled,
            JsonSerializer.Serialize(snapshot));
    }

    private static double ToUnixSeconds(DateTimeOffset value)
    {
        return value.ToUnixTimeMilliseconds() / 1000.0;
    }

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }

    private sealed class PricingResolution
    {
        public PricingResolution(ModelInfo model, ModelPricingPlan? plan, string reason)
        {
            Model = model;
            Plan = plan;
            Reason = reason;
        }

        public PricingResolution(ChannelModelInfo model, ModelPricingPlan? plan, string reason)
        {
            ChannelModel = model;
            Plan = plan;
            Reason = reason;
        }

        public PricingResolution(string reason)
        {
            Reason = reason;
        }

        public ModelInfo? Model { get; }

        public ChannelModelInfo? ChannelModel { get; }

        public ModelPricingPlan? Plan { get; }

        public string Reason { get; }

        public bool HasModel => Model is not null || ChannelModel is not null;

        public Guid ProviderId => ChannelModel?.ProviderId ?? Model!.ProviderId;

        public string ModelKey => ChannelModel?.ModelKey ?? Model!.ModelKey;

        public string MatchType => ChannelModel?.MatchType ?? Model!.MatchType;

        public string MatchPattern => ChannelModel?.MatchPattern ?? Model!.MatchPattern;
    }

    private sealed class MatchScore
    {
        public MatchScore(int priority, int patternLength)
        {
            Priority = priority;
            PatternLength = patternLength;
        }

        public int Priority { get; }

        public int PatternLength { get; }
    }

    private sealed class PricingTier
    {
        [JsonPropertyName("up_to")]
        public long? UpTo { get; set; }

        [JsonPropertyName("unit_price")]
        public decimal UnitPrice { get; set; }
    }
}

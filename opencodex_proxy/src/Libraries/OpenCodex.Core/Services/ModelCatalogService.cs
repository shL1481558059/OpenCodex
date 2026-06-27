using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class ModelCatalogService : IModelCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRepository<ModelProvider> _providers;
    private readonly IRepository<ModelInfo> _models;
    private readonly IRepository<ChannelModelInfo> _channelModels;
    private readonly IRepository<ModelPricingPlan> _plans;
    private readonly IRepository<ModelPricingRule> _rules;
    private readonly IRepository<ChannelModelMapping> _mappings;
    private readonly IRepository<Channel> _channels;
    private readonly IRepository<ModelPricing> _legacyPricing;
    private readonly IWorkContext _workContext;

    public ModelCatalogService(
        IRepository<ModelProvider> providers,
        IRepository<ModelInfo> models,
        IRepository<ChannelModelInfo> channelModels,
        IRepository<ModelPricingPlan> plans,
        IRepository<ModelPricingRule> rules,
        IRepository<ChannelModelMapping> mappings,
        IRepository<Channel> channels,
        IRepository<ModelPricing> legacyPricing,
        IWorkContext workContext)
    {
        _providers = providers;
        _models = models;
        _channelModels = channelModels;
        _plans = plans;
        _rules = rules;
        _mappings = mappings;
        _channels = channels;
        _legacyPricing = legacyPricing;
        _workContext = workContext;
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

    public ApiOpResult<ModelInfoListResponse> ListModels(
        string? query,
        string? providerCode,
        bool? enabled)
    {
        var providerById = _providers.TableNoTracking
            .ToDictionary(provider => provider.Id);
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

        return ApiOpResult<ModelInfoListResponse>.Succeed(new ModelInfoListResponse(
            models.Select(model => ToModelResponse(model, providerById)).ToList()));
    }

    public ApiOpResult<ModelInfoResponsePayload> CreateModel(ModelInfoCreateRequest request)
    {
        try
        {
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

            return ApiOpResult<ModelInfoResponsePayload>.Succeed(
                new ModelInfoResponsePayload(ToModelResponse(model, ProviderMap())));
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

            return ApiOpResult<ModelInfoResponsePayload>.Succeed(
                new ModelInfoResponsePayload(ToModelResponse(model, ProviderMap())));
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

        model.Enabled = false;
        model.UpdatedAt = UnixTimeSeconds();
        _models.Update(model);
        return ApiOpResult<ModelInfoResponsePayload>.Succeed(
            new ModelInfoResponsePayload(ToModelResponse(model, ProviderMap())));
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

        var items = upstreamModels
            .Where(model => Normalize(model).Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .Select(upstreamModel =>
            {
                var globalModel = ResolveGlobalModel(upstreamModel);
                overrides.TryGetValue(upstreamModel, out var overrideModel);
                return new ChannelModelInfoListItemResponse(
                    upstreamModel,
                    overrideModel is not null,
                    globalModel is null ? null : ToModelResponse(globalModel, providerById),
                    overrideModel is null ? null : ToChannelModelResponse(overrideModel, providerById));
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

            return ApiOpResult<ChannelModelInfoResponsePayload>.Succeed(
                new ChannelModelInfoResponsePayload(ToChannelModelResponse(existing, ProviderMap())));
        }
        catch (ArgumentException exception)
        {
            return ChannelModelValidationFailure(exception.Message);
        }
    }

    public ApiOpResult RestoreChannelModelInfo(Guid channelId, Guid id)
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
        return ApiOpResult.Succeed();
    }

    public bool SupportsImage(Guid? channelId, string? upstreamModel, bool legacyMappingValue)
    {
        if (legacyMappingValue)
        {
            return true;
        }

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
                return SupportsImage(channelModel.CapabilitiesJson);
            }
        }

        var globalModel = ResolveGlobalModel(actualModel);
        return globalModel is not null && SupportsImage(globalModel.CapabilitiesJson);
    }

    public ApiOpResult<SeedModelCatalogResponse> SeedDefaults()
    {
        var now = UnixTimeSeconds();
        var providersInserted = EnsureDefaultProviders(now);
        var result = SeedDefaultModels(now);
        var migrated = MigrateLegacyPricing(now);

        return ApiOpResult<SeedModelCatalogResponse>.Succeed(new SeedModelCatalogResponse(
            providersInserted + migrated.ProvidersInserted,
            result.Inserted + migrated.Inserted,
            result.Updated + migrated.Updated,
            result.Skipped + migrated.Skipped));
    }

    public ModelPricingCalculationResult CalculateCost(
        Guid? channelId,
        string? requestModel,
        string? upstreamModel,
        string? responseModel,
        ModelUsageVector usage)
    {
        var resolution = ResolvePricing(channelId, upstreamModel);
        if (!resolution.HasModel || resolution.Plan is null)
        {
            return EmptyCalculation(resolution.Reason);
        }

        var rules = _rules.TableNoTracking
            .Where(rule => rule.PricingPlanId == resolution.Plan.Id && rule.Enabled)
            .ToList();
        if (rules.Count == 0)
        {
            return EmptyCalculation("pricing_plan_has_no_rules");
        }

        var providerId = resolution.ProviderId;
        var modelInfoId = resolution.Model?.Id;
        var channelModelInfoId = resolution.ChannelModel?.Id;
        var modelKey = resolution.ModelKey;
        var matchType = resolution.MatchType;
        var matchPattern = resolution.MatchPattern;
        var total = 0m;
        var snapshotRules = new List<ModelPricingSnapshotRule>();
        foreach (var rule in rules)
        {
            var quantity = Quantity(rule, usage);
            var cost = CalculateRuleCost(rule, quantity);
            total += cost;
            snapshotRules.Add(new ModelPricingSnapshotRule(
                rule.BillingItem,
                rule.BillingMode,
                quantity,
                rule.UnitPrice,
                cost));
        }

        var provider = _providers.TableNoTracking.FirstOrDefault(item => item.Id == providerId);
        var snapshot = new ModelPricingSnapshot(
            resolution.Reason,
            resolution.Plan.Currency,
            total,
            modelInfoId,
            channelModelInfoId,
            resolution.Plan.Id,
            provider?.Code,
            modelKey,
            matchType,
            matchPattern,
            snapshotRules);
        var snapshotJson = JsonSerializer.Serialize(snapshot);

        return new ModelPricingCalculationResult(
            total,
            resolution.Plan.Currency,
            modelInfoId,
            channelModelInfoId,
            resolution.Plan.Id,
            provider?.Code,
            modelKey,
            matchType,
            matchPattern,
            resolution.Reason,
            snapshotJson);
    }

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
                return new PricingResolution(
                    channelModel,
                    FindPlanForChannelModel(channelModel.Id, channelId.Value),
                    "channel_model_override");
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

        return _channelModels.TableNoTracking
            .Where(model => model.ChannelId == channelId && model.Enabled)
            .AsEnumerable()
            .FirstOrDefault(model => string.Equals(
                model.UpstreamModel,
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    private ModelInfo? ResolveGlobalModel(string modelName)
    {
        var normalized = Normalize(modelName);
        if (normalized.Length == 0)
        {
            return null;
        }

        var providerSort = _providers.TableNoTracking.ToDictionary(provider => provider.Id, provider => provider.SortOrder);
        return _models.TableNoTracking
            .Where(model => model.Enabled && model.Scope == ModelInfoScopes.Global && model.ChannelId == null)
            .AsEnumerable()
            .Select(model => new
            {
                Model = model,
                Rank = MatchRank(model.MatchType, model.MatchPattern, normalized),
                ProviderSort = providerSort.TryGetValue(model.ProviderId, out var sort) ? sort : int.MaxValue
            })
            .Where(item => item.Rank is not null)
            .OrderBy(item => item.Rank!.Priority)
            .ThenByDescending(item => item.Rank!.PatternLength)
            .ThenBy(item => item.ProviderSort)
            .ThenBy(item => item.Model.ModelKey, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Model)
            .FirstOrDefault();
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

    private static decimal CalculateRuleCost(ModelPricingRule rule, int quantity)
    {
        if (quantity <= 0)
        {
            return 0m;
        }

        return rule.BillingMode switch
        {
            ModelBillingModes.PerRequest => quantity * rule.UnitPrice,
            ModelBillingModes.PerMillionTokens => quantity * rule.UnitPrice / 1_000_000m,
            ModelBillingModes.TieredTokens => CalculateTieredCost(quantity, rule.TiersJson),
            _ => 0m
        };
    }

    private static decimal CalculateTieredCost(int quantity, string tiersJson)
    {
        var tiers = DeserializeTiers(tiersJson);
        if (tiers.Count == 0)
        {
            return 0m;
        }

        var remaining = quantity;
        var previousLimit = 0L;
        var total = 0m;
        foreach (var tier in tiers.OrderBy(tier => tier.UpTo ?? long.MaxValue))
        {
            if (remaining <= 0)
            {
                break;
            }

            var tierLimit = tier.UpTo ?? long.MaxValue;
            var tierSize = tierLimit == long.MaxValue
                ? remaining
                : (int)Math.Max(0, Math.Min(remaining, tierLimit - previousLimit));
            total += tierSize * tier.UnitPrice / 1_000_000m;
            remaining -= tierSize;
            previousLimit = tierLimit;
        }

        return total;
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

    private int EnsureDefaultProviders(double now)
    {
        var defaults = OpenCodexModelCatalogDefaults.Providers();
        var defaultCodes = defaults
            .Select(provider => provider.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = _providers.Table
            .ToDictionary(provider => provider.Code, StringComparer.OrdinalIgnoreCase);
        var inserted = 0;
        foreach (var provider in defaults)
        {
            if (existing.TryGetValue(provider.Code, out var current))
            {
                if (string.Equals(current.Source, ModelCatalogSources.SystemDefault, StringComparison.Ordinal)
                    && (!string.Equals(current.Name, provider.Name, StringComparison.Ordinal)
                        || current.SortOrder != provider.SortOrder))
                {
                    current.Name = provider.Name;
                    current.SortOrder = provider.SortOrder;
                    current.UpdatedAt = now;
                    _providers.Update(current);
                }

                continue;
            }

            var created = new ModelProvider
            {
                Code = provider.Code,
                Name = provider.Name,
                Enabled = true,
                SortOrder = provider.SortOrder,
                Source = ModelCatalogSources.SystemDefault,
                CreatedAt = now,
                UpdatedAt = now
            };
            _providers.Insert(created);
            existing[created.Code] = created;
            inserted++;
        }

        foreach (var provider in existing.Values)
        {
            if (string.Equals(provider.Source, ModelCatalogSources.SystemDefault, StringComparison.Ordinal)
                && provider.Enabled
                && !defaultCodes.Contains(provider.Code))
            {
                provider.Enabled = false;
                provider.UpdatedAt = now;
                _providers.Update(provider);
            }
        }

        return inserted;
    }

    private (int Inserted, int Updated, int Skipped) SeedDefaultModels(double now)
    {
        var providers = ProviderMapByCode();
        var inserted = 0;
        var updated = 0;
        var skipped = 0;
        foreach (var source in OpenCodexModelCatalogDefaults.Models())
        {
            if (!providers.TryGetValue(source.ProviderCode, out var provider))
            {
                skipped++;
                continue;
            }

            var existing = _models.Table.FirstOrDefault(model =>
                model.Scope == ModelInfoScopes.Global
                && model.ChannelId == null
                && model.ModelKey == source.ModelKey);
            if (existing is null)
            {
                var created = new ModelInfo
                {
                    Scope = ModelInfoScopes.Global,
                    ProviderId = provider.Id,
                    ChannelId = null,
                    ModelKey = source.ModelKey,
                    DisplayName = source.DisplayName,
                    Description = source.Description,
                    MatchType = source.MatchType,
                    MatchPattern = source.MatchPattern,
                    CatalogJson = source.CatalogJson,
                    CapabilitiesJson = source.CapabilitiesJson,
                    Enabled = true,
                    Source = ModelCatalogSources.SystemDefault,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _models.Insert(created);
                ReplacePricing(created, source, now);
                inserted++;
                continue;
            }

            if (!string.Equals(existing.Source, ModelCatalogSources.SystemDefault, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            existing.ProviderId = provider.Id;
            existing.DisplayName = source.DisplayName;
            existing.Description = source.Description;
            existing.MatchType = source.MatchType;
            existing.MatchPattern = source.MatchPattern;
            existing.CatalogJson = source.CatalogJson;
            existing.CapabilitiesJson = source.CapabilitiesJson;
            existing.UpdatedAt = now;
            _models.Update(existing);
            ReplacePricing(existing, source, now);
            updated++;
        }

        return (inserted, updated, skipped);
    }

    private (int ProvidersInserted, int Inserted, int Updated, int Skipped) MigrateLegacyPricing(double now)
    {
        var providerByCode = ProviderMapByCode();
        var providersInserted = 0;
        var inserted = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var legacy in _legacyPricing.TableNoTracking.ToList())
        {
            var providerCode = NormalizeProviderCode(legacy.Vendor);
            if (providerCode.Length == 0)
            {
                skipped++;
                continue;
            }

            if (!providerByCode.TryGetValue(providerCode, out var provider))
            {
                provider = new ModelProvider
                {
                    Code = providerCode,
                    Name = providerCode,
                    Enabled = true,
                    SortOrder = 900,
                    Source = ModelCatalogSources.MigratedModelPricing,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _providers.Insert(provider);
                providerByCode[provider.Code] = provider;
                providersInserted++;
            }

            var modelKey = Normalize(legacy.ModelId);
            if (modelKey.Length == 0)
            {
                skipped++;
                continue;
            }

            var existing = _models.Table.FirstOrDefault(model =>
                model.Scope == ModelInfoScopes.Global
                && model.ChannelId == null
                && model.ModelKey == modelKey);
            if (existing is not null
                && !string.Equals(existing.Source, ModelCatalogSources.MigratedModelPricing, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            if (existing is null)
            {
                existing = new ModelInfo
                {
                    Scope = ModelInfoScopes.Global,
                    ProviderId = provider.Id,
                    ChannelId = null,
                    ModelKey = modelKey,
                    DisplayName = DisplayName(legacy.Name, modelKey),
                    Description = string.Empty,
                    MatchType = ModelMatchTypes.Exact,
                    MatchPattern = NormalizeMatchPattern(legacy.MatchPattern, modelKey),
                    CatalogJson = OpenCodexModelCatalogDefaults.CatalogJson(modelKey, DisplayName(legacy.Name, modelKey), false, 128000),
                    CapabilitiesJson = OpenCodexModelCatalogDefaults.CapabilitiesJson(false, 128000),
                    Enabled = legacy.Enabled,
                    Source = ModelCatalogSources.MigratedModelPricing,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _models.Insert(existing);
                inserted++;
            }
            else
            {
                existing.ProviderId = provider.Id;
                existing.DisplayName = DisplayName(legacy.Name, modelKey);
                existing.MatchPattern = NormalizeMatchPattern(legacy.MatchPattern, modelKey);
                existing.Enabled = legacy.Enabled;
                existing.UpdatedAt = now;
                _models.Update(existing);
                updated++;
            }

            ReplacePricing(existing, legacy, now);
        }

        return (providersInserted, inserted, updated, skipped);
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
            Enabled = request.Enabled,
            Source = source,
            CreatedAt = now,
            UpdatedAt = now
        };
        _plans.Insert(plan);

        var rules = NormalizeRules(request.Rules)
            .Select(rule => new ModelPricingRule
            {
                PricingPlanId = plan.Id,
                BillingItem = NormalizeBillingItem(rule.BillingItem),
                BillingMode = NormalizeBillingMode(rule.BillingMode),
                UnitPrice = ValidatePrice(rule.UnitPrice, "unit_price"),
                TiersJson = SerializeTiers(rule.Tiers),
                Enabled = rule.Enabled
            })
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
            Enabled = request.Enabled,
            Source = source,
            CreatedAt = now,
            UpdatedAt = now
        };
        _plans.Insert(plan);

        var rules = NormalizeRules(request.Rules)
            .Select(rule => new ModelPricingRule
            {
                PricingPlanId = plan.Id,
                BillingItem = NormalizeBillingItem(rule.BillingItem),
                BillingMode = NormalizeBillingMode(rule.BillingMode),
                UnitPrice = ValidatePrice(rule.UnitPrice, "unit_price"),
                TiersJson = SerializeTiers(rule.Tiers),
                Enabled = rule.Enabled
            })
            .ToList();
        if (rules.Count > 0)
        {
            _rules.Insert(rules);
        }
    }

    private void ReplacePricing(ModelInfo model, DefaultModelInfo source, double now)
    {
        RemovePlans(model.Id, model.ChannelId);
        var plan = new ModelPricingPlan
        {
            ModelInfoId = model.Id,
            ChannelId = null,
            Currency = source.Currency,
            Enabled = true,
            Source = ModelCatalogSources.SystemDefault,
            CreatedAt = now,
            UpdatedAt = now
        };
        _plans.Insert(plan);
        _rules.Insert(DefaultRules(plan.Id, source.InputPrice, source.OutputPrice, source.CacheWritePrice, source.CacheReadPrice));
    }

    private void ReplacePricing(ModelInfo model, ModelPricing source, double now)
    {
        RemovePlans(model.Id, model.ChannelId);
        var plan = new ModelPricingPlan
        {
            ModelInfoId = model.Id,
            ChannelId = null,
            Currency = "USD",
            Enabled = source.Enabled,
            Source = ModelCatalogSources.MigratedModelPricing,
            CreatedAt = now,
            UpdatedAt = now
        };
        _plans.Insert(plan);
        var cachePrice = Convert.ToDecimal(source.CachedInputPrice ?? source.InputPrice, CultureInfo.InvariantCulture);
        _rules.Insert(DefaultRules(
            plan.Id,
            Convert.ToDecimal(source.InputPrice, CultureInfo.InvariantCulture),
            Convert.ToDecimal(source.OutputPrice, CultureInfo.InvariantCulture),
            cachePrice,
            cachePrice));
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

    private static List<ModelPricingRule> DefaultRules(
        Guid pricingPlanId,
        decimal inputPrice,
        decimal outputPrice,
        decimal cacheWritePrice,
        decimal cacheReadPrice)
    {
        return
        [
            DefaultRule(pricingPlanId, ModelBillingItems.Input, inputPrice),
            DefaultRule(pricingPlanId, ModelBillingItems.Output, outputPrice),
            DefaultRule(pricingPlanId, ModelBillingItems.CacheWrite, cacheWritePrice),
            DefaultRule(pricingPlanId, ModelBillingItems.CacheRead, cacheReadPrice)
        ];
    }

    private static ModelPricingRule DefaultRule(Guid pricingPlanId, string billingItem, decimal unitPrice)
    {
        return new ModelPricingRule
        {
            PricingPlanId = pricingPlanId,
            BillingItem = billingItem,
            BillingMode = ModelBillingModes.PerMillionTokens,
            UnitPrice = unitPrice,
            TiersJson = "[]",
            Enabled = true
        };
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
        IReadOnlyDictionary<Guid, ModelProvider> providerById)
    {
        providerById.TryGetValue(model.ProviderId, out var provider);
        var plan = _plans.TableNoTracking
            .Where(item => item.ChannelModelInfoId == model.Id && item.ChannelId == model.ChannelId)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();

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
            plan is null ? null : ToPlanResponse(plan),
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

    private Dictionary<Guid, ModelProvider> ProviderMap()
    {
        return _providers.TableNoTracking.ToDictionary(provider => provider.Id);
    }

    private Dictionary<string, ModelProvider> ProviderMapByCode()
    {
        return _providers.Table.ToDictionary(provider => provider.Code, StringComparer.OrdinalIgnoreCase);
    }

    private int NextProviderSortOrder()
    {
        var currentMax = _providers.TableNoTracking
            .Select(provider => (int?)provider.SortOrder)
            .Max() ?? 0;
        return currentMax + 10;
    }

    private ModelInfoResponse ToModelResponse(ModelInfo model, IReadOnlyDictionary<Guid, ModelProvider> providerById)
    {
        providerById.TryGetValue(model.ProviderId, out var provider);
        var plan = _plans.TableNoTracking
            .Where(item => item.ModelInfoId == model.Id && item.ChannelId == model.ChannelId)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();

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
            plan is null ? null : ToPlanResponse(plan),
            model.CreatedAt,
            model.UpdatedAt);
    }

    private ModelPricingPlanResponse ToPlanResponse(ModelPricingPlan plan)
    {
        var rules = _rules.TableNoTracking
            .Where(rule => rule.PricingPlanId == plan.Id)
            .OrderBy(rule => rule.BillingItem)
            .AsEnumerable()
            .Select(rule => new ModelPricingRuleResponse(
                rule.Id,
                rule.BillingItem,
                rule.BillingMode,
                rule.UnitPrice,
                DeserializeList(rule.TiersJson),
                rule.Enabled))
            .ToList();

        return new ModelPricingPlanResponse(
            plan.Id,
            plan.ModelInfoId,
            plan.ChannelModelInfoId,
            plan.ChannelId,
            plan.Currency,
            plan.Enabled,
            plan.Source,
            rules,
            plan.CreatedAt,
            plan.UpdatedAt);
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
        IReadOnlyDictionary<Guid, ModelProvider> providerById,
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

    private static ModelPricingCalculationResult EmptyCalculation(string resolution)
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
            JsonSerializer.Serialize(snapshot));
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

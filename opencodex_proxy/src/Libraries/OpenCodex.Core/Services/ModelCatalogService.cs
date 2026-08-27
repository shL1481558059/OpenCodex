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

    public ApiOpResult<ModelInfoResponsePayload> ReadModelInfoById(Guid id)
    {
        if (id == Guid.Empty)
        {
            return ApiOpResult<ModelInfoResponsePayload>.Fail(400, "model id is required");
        }

        var providerById = _providers.TableNoTracking
            .ToDictionary(provider => provider.Id);
        var model = _models.TableNoTracking.FirstOrDefault(m => m.Id == id);
        if (model is null)
        {
            return ApiOpResult<ModelInfoResponsePayload>.Fail(404, "model not found");
        }

        return ApiOpResult<ModelInfoResponsePayload>.Succeed(
            new ModelInfoResponsePayload(ToModelResponse(model, providerById)));
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

        if (model.Enabled)
        {
            // 启用状态：执行停用（软删除）
            model.Enabled = false;
            model.UpdatedAt = UnixTimeSeconds();
            _models.Update(model);
            BumpPricingVersion();
            return ApiOpResult<ModelInfoResponsePayload>.Succeed(
                new ModelInfoResponsePayload(ToModelResponse(model, ProviderMap())));
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
        var providerById = _providers.TableNoTracking.ToDictionary(provider => provider.Id);
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
                new ChannelModelInfoResponsePayload(ToChannelModelResponse(existing, ProviderMap())));
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

   public async Task<ModelPricingCalculationResult> CalculateCostAsync(
       Guid? channelId,
       string? requestModel,
       string? upstreamModel,
       ModelUsageVector usage,
       DateTimeOffset billingInstant)
    {
        // 缓存定价解析(按 channelId + upstreamModel),扁平 DTO 规避 PricingResolution 不可序列化的问题。
        // rules/provider 为索引小查询,每次现查;usage 计算每请求不同,不可缓存。
        var cached = await ResolvePricingCachedAsync(channelId, upstreamModel);
        if (cached is null || !cached.HasModel || !cached.HasPlan)
        {
            return EmptyCalculation(cached?.Reason ?? "model_not_matched", billingInstant);
        }

        var planId = cached.PlanId!.Value;
        var rules = _rules.TableNoTracking
            .Where(rule => rule.PricingPlanId == planId && rule.Enabled)
            .ToList();
        if (rules.Count == 0)
        {
            return EmptyCalculation("pricing_plan_has_no_rules", billingInstant);
        }

        var providerId = cached.ProviderId!.Value;
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
            var cost = CalculateRuleCost(rule.BillingMode, quantity, unitPrice, tiersJson);
            total += cost;
            snapshotRules.Add(new ModelPricingSnapshotRule(
                rule.BillingItem,
                rule.BillingMode,
                quantity,
                unitPrice,
                cost,
                useOffPeak ? PricingPhases.OffPeak : PricingPhases.Peak));
        }

        var provider = _providers.TableNoTracking.FirstOrDefault(item => item.Id == providerId);
        var snapshot = new ModelPricingSnapshot(
            cached.Reason,
            cached.PlanCurrency,
            total,
            modelInfoId,
            channelModelInfoId,
            planId,
            provider?.Code,
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
            cached.PlanCurrency,
            modelInfoId,
            channelModelInfoId,
            planId,
            provider?.Code,
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

    private static CachedPricingResolution ToCached(PricingResolution resolution)
    {
        var hasModel = resolution.HasModel;
        return new CachedPricingResolution(
            hasModel,
            resolution.Plan is not null,
            resolution.Plan?.Id,
            resolution.Plan?.Currency,
            resolution.Plan?.TimeZoneId ?? string.Empty,
            resolution.Plan?.OffPeakWindowsJson ?? "[]",
            hasModel ? resolution.ProviderId : null,
            resolution.Model?.Id,
            resolution.ChannelModel?.Id,
            hasModel ? resolution.ModelKey : null,
            hasModel ? resolution.MatchType : null,
            hasModel ? resolution.MatchPattern : null,
            resolution.Reason);
    }

    private sealed record CachedPricingResolution(
        bool HasModel,
        bool HasPlan,
        Guid? PlanId,
        string? PlanCurrency,
        string TimeZoneId,
        string OffPeakWindowsJson,
        Guid? ProviderId,
        Guid? ModelInfoId,
        Guid? ChannelModelInfoId,
        string? ModelKey,
        string? MatchType,
        string? MatchPattern,
        string Reason);

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

    private static decimal CalculateRuleCost(
        string billingMode,
        int quantity,
        decimal unitPrice,
        string tiersJson)
    {
        if (quantity <= 0)
        {
            return 0m;
        }

        return billingMode switch
        {
            ModelBillingModes.PerRequest => quantity * unitPrice,
            ModelBillingModes.PerMillionTokens => quantity * unitPrice / 1_000_000m,
            ModelBillingModes.TieredTokens => CalculateTieredCost(quantity, tiersJson),
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
        ValidateOffPeakTierCoverage(
            rule.OffPeakEnabled,
            billingMode,
            rule.OffPeakTiers.Count);
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
        ValidateOffPeakTierCoverage(
            rule.OffPeakEnabled,
            billingMode,
            rule.OffPeakTiers.Count);
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

    // 阶梯计价开了峰谷却没有谷段阶梯,金额会静默算成 0,直接拒绝而不是猜一个回退。
    private static void ValidateOffPeakTierCoverage(
        bool offPeakEnabled,
        string billingMode,
        int offPeakTierCount)
    {
        if (offPeakEnabled
            && billingMode == ModelBillingModes.TieredTokens
            && offPeakTierCount == 0)
        {
            throw new ArgumentException(
                "off_peak_tiers is required when off_peak_enabled is true and billing_mode is tiered_tokens",
                "off_peak_tiers");
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
            ValidateOffPeakTierCoverage(rule.OffPeakEnabled, billingMode, rule.OffPeakTiers.Count);
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

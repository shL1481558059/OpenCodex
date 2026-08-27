using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Caching;
using OpenCodex.CoreBase.Caching;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
using StackExchange.Redis;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ModelCatalogServiceTests
{
    [Fact]
    public void CreateProviderCreatesManualProvider()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var service = CreateService(dbPath);
        var result = service.CreateProvider(new ModelProviderUpsertRequest
        {
            Code = "Custom.AI",
            Name = "Custom AI",
            SortOrder = 321,
            Enabled = true
        });

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Payload);
        Assert.Equal("custom.ai", result.Payload.Provider.Code);
        Assert.Equal("Custom AI", result.Payload.Provider.Name);
        Assert.Equal(ModelCatalogSources.Manual, result.Payload.Provider.Source);

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var provider = context.ModelProviders.Single(item => item.Code == "custom.ai");
            Assert.Equal("Custom AI", provider.Name);
            Assert.Equal(321, provider.SortOrder);
            Assert.True(provider.Enabled);
            Assert.Equal(ModelCatalogSources.Manual, provider.Source);
        }
    }

    [Fact]
    public void CreateProviderRejectsDuplicateCode()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            AddProvider(context, "custom", "Custom", ModelCatalogSources.Manual, 1);
        }

        var service = CreateService(dbPath);
        var result = service.CreateProvider(new ModelProviderUpsertRequest
        {
            Code = "CUSTOM",
            Name = "Other Custom",
            Enabled = true
        });

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Code);
    }

    [Fact]
    public void CatalogStartsEmptyWithoutManualData()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var service = CreateService(dbPath);
        var providers = service.ListProviders(includeDisabled: true);
        var models = service.ListModels(null, null, null);

        Assert.True(providers.Succeeded);
        Assert.NotNull(providers.Payload);
        Assert.Empty(providers.Payload!.Providers);
        Assert.True(models.Succeeded);
        Assert.NotNull(models.Payload);
        Assert.Empty(models.Payload!.Models);
    }

    [Fact]
    public async Task CalculateCostUsesMatchPriority()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddModel(context, provider.Id, "exact", ModelMatchTypes.Exact, "model-x", 1m);
            AddModel(context, provider.Id, "prefix", ModelMatchTypes.Prefix, "model-", 2m);
            AddModel(context, provider.Id, "suffix", ModelMatchTypes.Suffix, "-x", 3m);
            AddModel(context, provider.Id, "contains", ModelMatchTypes.Contains, "x", 4m);
            context.SaveChanges();
        }

        var service = CreateService(dbPath);

        Assert.Equal(1m, (await service.CalculateCostAsync(null, null, "model-x", Tokens(1_000_000))).Cost);
        Assert.Equal(2m, (await service.CalculateCostAsync(null, null, "model-y", Tokens(1_000_000))).Cost);
        Assert.Equal(3m, (await service.CalculateCostAsync(null, null, "other-x", Tokens(1_000_000))).Cost);
        Assert.Equal(4m, (await service.CalculateCostAsync(null, null, "other-x-other", Tokens(1_000_000))).Cost);
    }

    [Fact]
    public async Task UpdateModelInvalidatesCachedPricingImmediately()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var cache = new InMemoryCacheService();
        var service = CreateService(dbPath, cache);
        var provider = service.CreateProvider(new ModelProviderUpsertRequest
        {
            Code = "cache-test",
            Name = "Cache Test",
            Enabled = true
        });
        Assert.True(provider.Succeeded);

        var created = service.CreateModel(ModelRequest("cache-model", 1m));
        Assert.True(created.Succeeded);
        Assert.NotNull(created.Payload);

        var first = await service.CalculateCostAsync(
            null,
            "cache-model",
            "cache-model",
            Tokens(1_000_000));
        Assert.Equal(1m, first.Cost);

        var updated = service.UpdateModel(created.Payload!.Model.Id, ModelRequest("cache-model", 2m));
        Assert.True(updated.Succeeded);

        var second = await service.CalculateCostAsync(
            null,
            "cache-model",
            "cache-model",
            Tokens(1_000_000));
        Assert.Equal(2m, second.Cost);
    }

    [Fact]
    public async Task RedisReconnectDoesNotRestoreStalePricingCache()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var redis = new TestRedisConnectionProvider(1_000_000);
        var service = CreateService(dbPath, new InMemoryCacheService(), redis);
        var provider = service.CreateProvider(new ModelProviderUpsertRequest
        {
            Code = "cache-test",
            Name = "Cache Test",
            Enabled = true
        });
        Assert.True(provider.Succeeded);

        var created = service.CreateModel(ModelRequest("cache-model", 1m));
        Assert.True(created.Succeeded);
        Assert.NotNull(created.Payload);

        var first = await service.CalculateCostAsync(
            null,
            "cache-model",
            "cache-model",
            Tokens(1_000_000));
        Assert.Equal(1m, first.Cost);
        var redisVersionBeforeDisconnect = redis.Version;

        redis.Available = false;
        var updated = service.UpdateModel(created.Payload!.Model.Id, ModelRequest("cache-model", 2m));
        Assert.True(updated.Succeeded);

        var whileDisconnected = await service.CalculateCostAsync(
            null,
            "cache-model",
            "cache-model",
            Tokens(1_000_000));
        Assert.Equal(2m, whileDisconnected.Cost);

        redis.Available = true;
        var afterReconnect = await service.CalculateCostAsync(
            null,
            "cache-model",
            "cache-model",
            Tokens(1_000_000));
        Assert.Equal(2m, afterReconnect.Cost);
        Assert.True(redis.Version > redisVersionBeforeDisconnect);
    }

    [Fact]
    public async Task RedisReconnectUsesDistinctVersionAfterLocalAndExternalOfflineMutations()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var redis = new TestRedisConnectionProvider(10_000_000);
        var service = CreateService(dbPath, new InMemoryCacheService(), redis);
        var provider = service.CreateProvider(new ModelProviderUpsertRequest
        {
            Code = "cache-test",
            Name = "Cache Test",
            Enabled = true
        });
        Assert.True(provider.Succeeded);

        var created = service.CreateModel(ModelRequest("cache-model", 1m));
        Assert.True(created.Succeeded);
        Assert.NotNull(created.Payload);

        redis.Available = false;
        var updated = service.UpdateModel(created.Payload!.Model.Id, ModelRequest("cache-model", 2m));
        Assert.True(updated.Succeeded);
        Assert.Equal(2m, (await service.CalculateCostAsync(
            null,
            "cache-model",
            "cache-model",
            Tokens(1_000_000))).Cost);

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var plans = context.ModelPricingPlans
                .Where(plan => plan.ModelInfoId == created.Payload.Model.Id)
                .ToList();
            var planIds = plans.Select(plan => plan.Id).ToList();
            context.ModelPricingRules.RemoveRange(
                context.ModelPricingRules.Where(rule => planIds.Contains(rule.PricingPlanId)));
            context.ModelPricingPlans.RemoveRange(plans);
            context.SaveChanges();
            AddPlan(context, created.Payload.Model.Id, null, 3m);
        }

        redis.Available = true;
        var afterReconnect = await service.CalculateCostAsync(
            null,
            "cache-model",
            "cache-model",
            Tokens(1_000_000));
        Assert.Equal(3m, afterReconnect.Cost);
    }

    [Fact]
    public async Task CreateAndDeleteModelInvalidateCachedPricingImmediately()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var service = CreateService(dbPath, new InMemoryCacheService());
        var provider = service.CreateProvider(new ModelProviderUpsertRequest
        {
            Code = "cache-test",
            Name = "Cache Test",
            Enabled = true
        });
        Assert.True(provider.Succeeded);

        var beforeCreate = await service.CalculateCostAsync(
            null,
            "new-cache-model",
            "new-cache-model",
            Tokens(1_000_000));
        Assert.Equal(0m, beforeCreate.Cost);

        var created = service.CreateModel(ModelRequest("new-cache-model", 3m));
        Assert.True(created.Succeeded);
        Assert.NotNull(created.Payload);

        var afterCreate = await service.CalculateCostAsync(
            null,
            "new-cache-model",
            "new-cache-model",
            Tokens(1_000_000));
        Assert.Equal(3m, afterCreate.Cost);

        var deleted = service.DeleteModel(created.Payload!.Model.Id);
        Assert.True(deleted.Succeeded);

        var afterDelete = await service.CalculateCostAsync(
            null,
            "new-cache-model",
            "new-cache-model",
            Tokens(1_000_000));
        Assert.Equal(0m, afterDelete.Cost);
    }

    [Fact]
    public void BatchModelsDisablesAndEnablesModels()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var service = CreateService(dbPath);
        var provider = service.CreateProvider(new ModelProviderUpsertRequest
        {
            Code = "batch-test",
            Name = "Batch Test",
            Enabled = true
        });
        Assert.True(provider.Succeeded);

        var first = service.CreateModel(BatchModelRequest("batch-a", true));
        var second = service.CreateModel(BatchModelRequest("batch-b", true));
        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);

        var disable = service.BatchModels(new ModelBatchActionRequest
        {
            Action = "disable",
            Ids = [first.Payload!.Model.Id, second.Payload!.Model.Id]
        });
        Assert.True(disable.Succeeded);
        Assert.Equal(2, disable.Payload!.UpdatedIds.Count);
        Assert.Empty(disable.Payload.DeletedIds);
        Assert.Empty(disable.Payload.Errors);

        var listedAfterDisable = service.ListModels(null, null, false);
        Assert.NotNull(listedAfterDisable.Payload);
        Assert.Equal(2, listedAfterDisable.Payload!.Models.Count(model => !model.Enabled));

        var enable = service.BatchModels(new ModelBatchActionRequest
        {
            Action = "enable",
            Ids = [first.Payload.Model.Id, second.Payload.Model.Id]
        });
        Assert.True(enable.Succeeded);
        Assert.Equal(2, enable.Payload!.UpdatedIds.Count);

        var listedAfterEnable = service.ListModels(null, null, true);
        Assert.NotNull(listedAfterEnable.Payload);
        Assert.Equal(2, listedAfterEnable.Payload!.Models.Count(model => model.Enabled));
    }

    [Fact]
    public void BatchModelsDeleteOnlyDisabledModelsAndSkipsEnabledOnes()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var service = CreateService(dbPath);
        var provider = service.CreateProvider(new ModelProviderUpsertRequest
        {
            Code = "batch-test",
            Name = "Batch Test",
            Enabled = true
        });
        Assert.True(provider.Succeeded);

        var disabled = service.CreateModel(BatchModelRequest("batch-del", false));
        var enabled = service.CreateModel(BatchModelRequest("batch-keep", true));
        Assert.True(disabled.Succeeded);
        Assert.True(enabled.Succeeded);

        var result = service.BatchModels(new ModelBatchActionRequest
        {
            Action = "delete",
            Ids = [disabled.Payload!.Model.Id, enabled.Payload!.Model.Id]
        });
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Payload!.DeletedIds.Count);
        Assert.Contains(disabled.Payload.Model.Id, result.Payload.DeletedIds);
        Assert.Single(result.Payload.Errors);
        Assert.Contains("enabled", result.Payload.Errors[0]);

        var models = service.ListModels(null, null, null);
        Assert.NotNull(models.Payload);
        Assert.Single(models.Payload!.Models);
        Assert.Equal("batch-keep", models.Payload.Models[0].ModelKey);
    }

    [Fact]
    public void BatchModelsRejectsInvalidActionAndEmptyIds()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var service = CreateService(dbPath);
        var invalidAction = service.BatchModels(new ModelBatchActionRequest
        {
            Action = "explode",
            Ids = [Guid.NewGuid()]
        });
        Assert.False(invalidAction.Succeeded);
        Assert.Equal(400, invalidAction.Code);

        var emptyIds = service.BatchModels(new ModelBatchActionRequest
        {
            Action = "disable",
            Ids = []
        });
        Assert.False(emptyIds.Succeeded);
        Assert.Equal(400, emptyIds.Code);
    }

    [Fact]
    public async Task CalculateCostUsesUpstreamModelForGlobalPricing()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddModel(context, provider.Id, "request-model", ModelMatchTypes.Exact, "request-model", 1m);
            AddModel(context, provider.Id, "upstream-model", ModelMatchTypes.Exact, "upstream-model", 7m);
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = await service.CalculateCostAsync(null, "request-model", "upstream-model", Tokens(1_000_000));

        Assert.Equal(7m, result.Cost);
        Assert.Equal("global_model_match", result.Resolution);
        Assert.Equal("upstream-model", result.ModelKey);
    }

   [Fact]
    public async Task CalculateCostUsesUpstreamModelForPricing()
   {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddModel(context, provider.Id, "upstream-model", ModelMatchTypes.Exact, "upstream-model", 2m);
            AddModel(context, provider.Id, "response-model", ModelMatchTypes.Exact, "response-model", 9m);
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = await service.CalculateCostAsync(null, "request-model", "upstream-model", Tokens(1_000_000));

        Assert.Equal(2m, result.Cost);
        Assert.Equal("global_model_match", result.Resolution);
        Assert.Equal("upstream-model", result.ModelKey);
    }

    [Fact]
    public async Task CalculateCostUsesChannelModelInfoByUpstreamModel()
    {
        var dbPath = CreateDbPath();
        var channelId = Guid.NewGuid();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddModel(context, provider.Id, "upstream-model", ModelMatchTypes.Exact, "upstream-model", 1m);
            AddChannelModel(
                context,
                channelId,
                provider.Id,
                "upstream-model",
                "channel-upstream-model",
                ModelMatchTypes.Exact,
                "upstream-model",
                9m);
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = await service.CalculateCostAsync(channelId, "request-alias", "upstream-model", Tokens(1_000_000));

        Assert.Equal(9m, result.Cost);
        Assert.Equal("channel_model_override", result.Resolution);
        Assert.Equal("channel-upstream-model", result.ModelKey);
        Assert.Null(result.ModelInfoId);
        Assert.NotNull(result.ChannelModelInfoId);
    }

    [Fact]
    public async Task CalculateCostDoesNotFallbackToRequestModel()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddModel(context, provider.Id, "request-model", ModelMatchTypes.Exact, "request-model", 5m);
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = await service.CalculateCostAsync(null, "request-model", "missing-upstream-model", Tokens(1_000_000));

        Assert.Equal(0m, result.Cost);
        Assert.Equal("model_not_matched", result.Resolution);
    }

    [Fact]
    public async Task CalculateCostIgnoresLegacyChannelMappingPricingFields()
    {
        var dbPath = CreateDbPath();
        var channelId = Guid.NewGuid();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            var model = AddModel(context, provider.Id, "model-a", ModelMatchTypes.Exact, "model-a", 1m);
            var overridePlan = AddPlan(context, model.Id, channelId, 9m);
            context.ChannelModelMappings.Add(new ChannelModelMapping
            {
                ChannelId = channelId,
                Position = 0,
               RequestModel = "model-a",
               UpstreamModel = "model-a",
               Enabled = true,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = await service.CalculateCostAsync(channelId, "model-a", "model-a", Tokens(1_000_000));

        Assert.Equal(1m, result.Cost);
        Assert.Equal("global_model_match", result.Resolution);
    }

    [Fact]
    public async Task ChannelModelInfoManagementOverridesAndRestoresGlobalPricing()
    {
        var dbPath = CreateDbPath();
        var channelId = Guid.NewGuid();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddChannel(context, channelId, "test-channel", "upstream-model");
            AddModel(context, provider.Id, "global-model", ModelMatchTypes.Exact, "upstream-model", 1m);
            context.SaveChanges();
        }

        var service = CreateService(dbPath, new InMemoryCacheService());
        var listed = service.ListChannelModelInfos(channelId);

        Assert.True(listed.Succeeded);
        var item = Assert.Single(listed.Payload!.Models);
        Assert.False(item.Overridden);
        Assert.Equal("global-model", item.GlobalModel?.ModelKey);

        var initialCost = await service.CalculateCostAsync(
            channelId,
            "request-model",
            "upstream-model",
            Tokens(1_000_000));
        Assert.Equal(1m, initialCost.Cost);

        var saved = service.UpsertChannelModelInfo(channelId, new ChannelModelInfoUpsertRequest
        {
            UpstreamModel = "upstream-model",
            ProviderCode = "test",
            ModelKey = "channel-model",
            DisplayName = "Channel Model",
            MatchType = ModelMatchTypes.Exact,
            MatchPattern = "upstream-model",
            Capabilities = new Dictionary<string, object?> { ["supports_image"] = true },
            Pricing = new ModelPricingPlanRequest
            {
                Currency = "USD",
                Rules =
                [
                    new ModelPricingRuleRequest
                    {
                        BillingItem = ModelBillingItems.Input,
                        BillingMode = ModelBillingModes.PerMillionTokens,
                        UnitPrice = 7m,
                        Enabled = true
                    }
                ]
            }
        });

        Assert.True(saved.Succeeded);
        var overrideCost = await service.CalculateCostAsync(channelId, "request-model", "upstream-model", Tokens(1_000_000));
        Assert.Equal(7m, overrideCost.Cost);
        Assert.Equal("channel_model_override", overrideCost.Resolution);

        listed = service.ListChannelModelInfos(channelId);
        item = Assert.Single(listed.Payload!.Models);
        Assert.True(item.Overridden);
        Assert.Equal("channel-model", item.OverrideModel?.ModelKey);

        var restored = service.DeleteChannelModelInfo(channelId, saved.Payload!.Model.Id);

        Assert.True(restored.Succeeded);
        var globalCost = await service.CalculateCostAsync(channelId, "request-model", "upstream-model", Tokens(1_000_000));
        Assert.Equal(1m, globalCost.Cost);
        Assert.Equal("global_model_match", globalCost.Resolution);

        listed = service.ListChannelModelInfos(channelId);
        item = Assert.Single(listed.Payload!.Models);
        Assert.False(item.Overridden);
    }

    [Fact]
    public void ModelCatalogRoundTripPreservesCatalogAndPricingWithoutChannelOverrides()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context, "openai", "OpenAI", ModelCatalogSources.Manual, 10);
            AddProvider(context, "disabled", "Disabled", ModelCatalogSources.Manual, 20).Enabled = false;
            context.SaveChanges();
            var model = AddModel(
                context,
                provider.Id,
                "gpt-test",
                ModelMatchTypes.Exact,
                "gpt-test",
                1.25m);
            model.DisplayName = "GPT Test";
            model.Description = "Test model";
            model.CatalogJson = """{"slug":"gpt-test"}""";
            model.CapabilitiesJson = """{"supports_image":true}""";
            var plan = context.ModelPricingPlans.Single(item => item.ModelInfoId == model.Id);
            plan.Currency = "USD";
            context.SaveChanges();

            var channelId = Guid.NewGuid();
            AddChannel(context, channelId, "Channel", "gpt-test");
            AddChannelModel(
                context,
                channelId,
                provider.Id,
                "gpt-test",
                "channel-model",
                ModelMatchTypes.Exact,
                "gpt-test",
                9m);
        }

        var service = CreateService(dbPath);
        var exported = service.ExportModelCatalog();
        var existingProviderCount = exported.Payload.Providers.Count;

        Assert.True(exported.Succeeded);
        Assert.NotNull(exported.Payload);
        Assert.Equal("model_catalog", exported.Payload.Type);
        Assert.Equal(1, exported.Payload.Version);
        Assert.Equal(2, exported.Payload.Providers.Count);
        Assert.Contains(exported.Payload.Providers, item => item.Code == "disabled" && !item.Enabled);
        var exportedModel = Assert.Single(exported.Payload.Models, item => item.ModelKey == "gpt-test");
        Assert.Equal("openai", exportedModel.ProviderCode);
        Assert.Equal("GPT Test", exportedModel.DisplayName);
        Assert.Equal("Test model", exportedModel.Description);
        Assert.Equal(ModelMatchTypes.Exact, exportedModel.MatchType);
        Assert.Equal("gpt-test", exportedModel.MatchPattern);
        Assert.True(exportedModel.Catalog.ContainsKey("slug"));
        Assert.True(exportedModel.Capabilities.TryGetValue("supports_image", out var supportsImage));
        Assert.True((bool)supportsImage!);
        Assert.Single(exportedModel.Pricing!.Rules);
        Assert.Equal(1.25m, exportedModel.Pricing.Rules[0].UnitPrice);

        exported.Payload.Providers.Add(new ModelCatalogProviderTransfer
        {
            Code = "new-provider",
            Name = "New Provider",
            Enabled = false,
            SortOrder = 30
        });

        var dryRun = service.ImportModelCatalog(exported.Payload, dryRun: true);
        Assert.True(dryRun.Succeeded);
        Assert.True(dryRun.Payload!.DryRun);
        Assert.Equal(1, dryRun.Payload.Providers.Created);
        Assert.Equal(2, dryRun.Payload.Providers.Unchanged);
        Assert.Equal(1, dryRun.Payload.Models.Unchanged);
        Assert.Equal(0, dryRun.Payload.ErrorCount);

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            Assert.DoesNotContain(verify.ModelProviders, item => item.Code == "new-provider");
        }

        var imported = service.ImportModelCatalog(exported.Payload, dryRun: false);
        Assert.True(imported.Succeeded);
        Assert.False(imported.Payload!.DryRun);
        Assert.Equal(1, imported.Payload.Providers.Created);
        Assert.Equal(1, imported.Payload.Models.Unchanged);

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            Assert.Single(verify.ModelProviders, item => item.Code == "new-provider");
            var provider = verify.ModelProviders.Single(item => item.Code == "openai");
            var model = verify.ModelInfos.Single(item => item.ModelKey == "gpt-test");
            Assert.Equal(provider.Id, model.ProviderId);
            Assert.Equal("GPT Test", model.DisplayName);
            Assert.True(verify.ChannelModelInfos.Any(item => item.ModelKey == "channel-model"));
        }

        var secondDryRun = service.ImportModelCatalog(exported.Payload, dryRun: true);
        Assert.True(secondDryRun.Succeeded);
        Assert.Equal(0, secondDryRun.Payload!.Providers.Created);
        Assert.Equal(existingProviderCount + 1, secondDryRun.Payload.Providers.Unchanged);
        Assert.Equal(1, secondDryRun.Payload.Models.Unchanged);
        Assert.Equal(0, secondDryRun.Payload.ErrorCount);
    }

    [Fact]
    public void ImportPreservesEmptyRulesAndCanDeletePricing()
    {
        var dbPath = CreateDbPath();
        Guid modelId;
        Guid planId;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            var model = AddModel(context, provider.Id, "empty-rules", ModelMatchTypes.Exact, "empty-rules", 1m);
            modelId = model.Id;
            planId = context.ModelPricingPlans.Single(item => item.ModelInfoId == modelId).Id;
        }

        var service = CreateService(dbPath);
        var emptyRules = ImportPayload();
        emptyRules.Models[0].ModelKey = "empty-rules";
        emptyRules.Models[0].MatchPattern = "empty-rules";
        emptyRules.Models[0].Pricing!.Rules.Clear();

        var emptyDryRun = service.ImportModelCatalog(emptyRules, dryRun: true);
        Assert.True(emptyDryRun.Succeeded);
        Assert.Equal(0, emptyDryRun.Payload!.ErrorCount);
        Assert.Equal(0, emptyDryRun.Payload.PricingDeleted);

        var emptyImport = service.ImportModelCatalog(emptyRules, dryRun: false);
        Assert.True(emptyImport.Succeeded);

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var plan = context.ModelPricingPlans.Single(item => item.ModelInfoId == modelId);
            Assert.Equal("USD", plan.Currency);
            Assert.True(plan.Enabled);
            Assert.Empty(context.ModelPricingRules.Where(item => item.PricingPlanId == plan.Id));
        }

        var deleted = ImportPayload();
        deleted.Models[0].ModelKey = "empty-rules";
        deleted.Models[0].MatchPattern = "empty-rules";
        deleted.Models[0].Pricing = null;
        var deletedDryRun = service.ImportModelCatalog(deleted, dryRun: true);
        Assert.True(deletedDryRun.Succeeded);
        Assert.Equal(1, deletedDryRun.Payload!.PricingDeleted);

        var deletedImport = service.ImportModelCatalog(deleted, dryRun: false);
        Assert.True(
            deletedImport.Succeeded,
            $"import failed: {deletedImport.Code} {deletedImport.Description ?? string.Empty}");
        Assert.Equal(1, deletedImport.Payload!.PricingDeleted);

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            Assert.DoesNotContain(context.ModelPricingPlans, item => item.ModelInfoId == modelId);
        }
    }

    [Fact]
    public void ImportRejectsInvalidDocumentsWithoutWriting()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddModel(context, provider.Id, "existing", ModelMatchTypes.Exact, "existing", 1m);
        }

        var service = CreateService(dbPath);
        var invalidDocuments = new List<(string Name, ModelCatalogTransferDocument Document)>
        {
            ("unknown type", MutateImport(payload => payload.Type = "other")),
            ("unknown version", MutateImport(payload => payload.Version = 2)),
            ("unknown provider", MutateImport(payload => payload.Models[0].ProviderCode = "missing")),
            ("invalid match type", MutateImport(payload => payload.Models[0].MatchType = "regex")),
            ("negative unit price", MutateImport(payload => payload.Models[0].Pricing!.Rules[0].UnitPrice = -1m)),
            ("negative tier price", MutateImport(payload =>
            {
                payload.Models[0].Pricing!.Rules[0].Tiers.Add(new ModelCatalogPricingTierTransfer { UnitPrice = -1m });
            })),
            ("negative tier limit", MutateImport(payload =>
            {
                payload.Models[0].Pricing!.Rules[0].Tiers.Add(new ModelCatalogPricingTierTransfer { UpTo = -1, UnitPrice = 1m });
            })),
            ("duplicate provider", DuplicateProviderPayload()),
            ("duplicate model", DuplicateModelPayload())
        };

        foreach (var (_, document) in invalidDocuments)
        {
            var result = service.ImportModelCatalog(document, dryRun: false);
            Assert.False(result.Succeeded);
            Assert.Equal(400, result.Code);
        }

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            Assert.Single(context.ModelProviders);
            Assert.Single(context.ModelInfos);
        }
    }

    [Fact]
    public void ImportRejectsExistingDuplicateGlobalModelKeys()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var first = AddProvider(context, "first", "First");
            var second = AddProvider(context, "second", "Second");
            AddModel(context, first.Id, "duplicate-model", ModelMatchTypes.Exact, "duplicate-model", 1m);
            AddModel(context, second.Id, "DUPLICATE-MODEL", ModelMatchTypes.Exact, "duplicate-model", 2m);
        }

        var service = CreateService(dbPath);
        var result = service.ImportModelCatalog(ImportPayload(), dryRun: true);

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Code);
        Assert.Contains("duplicate", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CalculateCostSplitsCacheWriteAndCacheRead()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            var model = AddModel(context, provider.Id, "cache-model", ModelMatchTypes.Exact, "cache-model", 1m);
            var plan = context.ModelPricingPlans.Single(item => item.ModelInfoId == model.Id);
            context.ModelPricingRules.RemoveRange(context.ModelPricingRules.Where(item => item.PricingPlanId == plan.Id));
            context.ModelPricingRules.AddRange(
                Rule(plan.Id, ModelBillingItems.Input, 1m),
                Rule(plan.Id, ModelBillingItems.CacheWrite, 2m),
                Rule(plan.Id, ModelBillingItems.CacheRead, 0.5m),
                Rule(plan.Id, ModelBillingItems.Output, 3m));
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = await service.CalculateCostAsync(
            null,
            null,
            "cache-model",
            new ModelUsageVector(
                inputTokens: 100,
                outputTokens: 10,
                cacheWriteTokens: 20,
                cacheReadTokens: 30));

        Assert.Equal(0.000135m, result.Cost);
    }

    private static ModelUsageVector Tokens(int inputTokens)
    {
        return new ModelUsageVector(inputTokens, 0, 0, 0);
    }

    private static ModelCatalogTransferDocument ImportPayload()
    {
        return new ModelCatalogTransferDocument
        {
            Type = "model_catalog",
            Version = 1,
            ExportedAt = "2026-08-17T12:00:00Z",
            Providers =
            [
                new ModelCatalogProviderTransfer
                {
                    Code = "test",
                    Name = "Test",
                    Enabled = true,
                    SortOrder = 1
                }
            ],
            Models =
            [
                new ModelCatalogModelTransfer
                {
                    ProviderCode = "test",
                    ModelKey = "existing",
                    DisplayName = "Existing",
                    Description = string.Empty,
                    MatchType = ModelMatchTypes.Exact,
                    MatchPattern = "existing",
                    Catalog = [],
                    Capabilities = [],
                    Enabled = true,
                    Pricing = new ModelCatalogPricingTransfer
                    {
                        Currency = "USD",
                        Enabled = true,
                        Rules =
                        [
                            new ModelCatalogPricingRuleTransfer
                            {
                                BillingItem = ModelBillingItems.Input,
                                BillingMode = ModelBillingModes.PerMillionTokens,
                                UnitPrice = 1m,
                                Enabled = true
                            }
                        ]
                    }
                }
            ]
        };
    }

    private static ModelCatalogTransferDocument MutateImport(Action<ModelCatalogTransferDocument> mutate)
    {
        var document = ImportPayload();
        mutate(document);
        return document;
    }

    private static ModelCatalogTransferDocument DuplicateProviderPayload()
    {
        var document = ImportPayload();
        document.Providers.Add(new ModelCatalogProviderTransfer
        {
            Code = "TEST",
            Name = "Duplicate"
        });
        return document;
    }

    private static ModelCatalogTransferDocument DuplicateModelPayload()
    {
        var document = ImportPayload();
        document.Models.Add(new ModelCatalogModelTransfer
        {
            ProviderCode = "test",
            ModelKey = "Existing",
            MatchType = ModelMatchTypes.Exact,
            MatchPattern = "existing"
        });
        return document;
    }

    private static ModelProvider AddProvider(
        IOpenCodexDbContext context,
        string code = "test",
        string name = "Test",
        string source = "test",
        int sortOrder = 1)
    {
        var provider = new ModelProvider
        {
            Code = code,
            Name = name,
            Enabled = true,
            SortOrder = sortOrder,
            Source = source,
            CreatedAt = 1,
            UpdatedAt = 1
        };
        context.ModelProviders.Add(provider);
        context.SaveChanges();
        return provider;
    }

    private static Channel AddChannel(
        IOpenCodexDbContext context,
        Guid channelId,
        string name,
        string upstreamModel)
    {
        var channel = new Channel
        {
            Id = channelId,
            OwnerUserId = TestUserId,
            Position = 0,
            Priority = 0,
            Name = name,
            Type = "chat",
            BaseUrl = "https://example.test/v1",
            ApiKey = "secret",
            AuthMode = "config",
            HeadersJson = "{}",
            TimeoutSeconds = 120,
            RetryCount = 0,
            Capacity = 3,
            CompatJson = "{}",
            ModelsJson = "[{\"model\":\"request-model\",\"upstream_model\":\"" + upstreamModel + "\"}]",
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        };
        context.Channels.Add(channel);
        context.ChannelModelMappings.Add(new ChannelModelMapping
        {
            ChannelId = channelId,
            Position = 0,
           RequestModel = "request-model",
           UpstreamModel = upstreamModel,
           Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        context.SaveChanges();
        return channel;
    }

    private static ModelInfo AddModel(
        IOpenCodexDbContext context,
        Guid providerId,
        string modelKey,
        string matchType,
        string matchPattern,
        decimal inputPrice,
        string scope = ModelInfoScopes.Global,
        Guid? channelId = null)
    {
        var model = new ModelInfo
        {
            Scope = scope,
            ProviderId = providerId,
            ChannelId = channelId,
            ModelKey = modelKey,
            DisplayName = modelKey,
            Description = string.Empty,
            MatchType = matchType,
            MatchPattern = matchPattern,
            CatalogJson = "{}",
            CapabilitiesJson = "{}",
            Enabled = true,
            Source = "test",
            CreatedAt = 1,
            UpdatedAt = 1
        };
        context.ModelInfos.Add(model);
        context.SaveChanges();
        AddPlan(context, model.Id, channelId, inputPrice);
        return model;
    }

    private static ChannelModelInfo AddChannelModel(
        IOpenCodexDbContext context,
        Guid channelId,
        Guid providerId,
        string upstreamModel,
        string modelKey,
        string matchType,
        string matchPattern,
        decimal inputPrice)
    {
        var model = new ChannelModelInfo
        {
            ChannelId = channelId,
            UpstreamModel = upstreamModel,
            ProviderId = providerId,
            ModelKey = modelKey,
            DisplayName = modelKey,
            Description = string.Empty,
            MatchType = matchType,
            MatchPattern = matchPattern,
            CatalogJson = "{}",
            CapabilitiesJson = "{}",
            Enabled = true,
            Source = "test",
            CreatedAt = 1,
            UpdatedAt = 1
        };
        context.ChannelModelInfos.Add(model);
        context.SaveChanges();
        AddChannelPlan(context, model.Id, channelId, inputPrice);
        return model;
    }

    private static ModelPricingPlan AddPlan(
        IOpenCodexDbContext context,
        Guid modelInfoId,
        Guid? channelId,
        decimal inputPrice)
    {
        var plan = new ModelPricingPlan
        {
            ModelInfoId = modelInfoId,
            ChannelId = channelId,
            Currency = "USD",
            Enabled = true,
            Source = "test",
            CreatedAt = 1,
            UpdatedAt = 1
        };
        context.ModelPricingPlans.Add(plan);
        context.SaveChanges();
        context.ModelPricingRules.Add(Rule(plan.Id, ModelBillingItems.Input, inputPrice));
        context.SaveChanges();
        return plan;
    }

    private static ModelPricingPlan AddChannelPlan(
        IOpenCodexDbContext context,
        Guid channelModelInfoId,
        Guid channelId,
        decimal inputPrice)
    {
        var plan = new ModelPricingPlan
        {
            ModelInfoId = null,
            ChannelModelInfoId = channelModelInfoId,
            ChannelId = channelId,
            Currency = "USD",
            Enabled = true,
            Source = "test",
            CreatedAt = 1,
            UpdatedAt = 1
        };
        context.ModelPricingPlans.Add(plan);
        context.SaveChanges();
        context.ModelPricingRules.Add(Rule(plan.Id, ModelBillingItems.Input, inputPrice));
        context.SaveChanges();
        return plan;
    }

    private static ModelPricingRule Rule(Guid planId, string item, decimal price)
    {
        return new ModelPricingRule
        {
            PricingPlanId = planId,
            BillingItem = item,
            BillingMode = ModelBillingModes.PerMillionTokens,
            UnitPrice = price,
            TiersJson = "[]",
            Enabled = true
        };
    }

    private static ModelCatalogService CreateService(
        string dbPath,
        ICacheService? cache = null,
        IRedisConnectionProvider? redis = null)
    {
        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        return new ModelCatalogService(
            new EfRepository<ModelProvider>(context),
            new EfRepository<ModelInfo>(context),
            new EfRepository<ChannelModelInfo>(context),
            new EfRepository<ModelPricingPlan>(context),
            new EfRepository<ModelPricingRule>(context),
            new EfRepository<ChannelModelMapping>(context),
            new EfRepository<Channel>(context),
            new TestWorkContext(TestUserId, "admin", "superadmin"),
            cache ?? new TestCacheService(),
            redis);
    }

    private static ModelInfoUpdateRequest ModelRequest(string modelKey, decimal inputPrice)
    {
        return new ModelInfoUpdateRequest
        {
            ProviderCode = "cache-test",
            ModelKey = modelKey,
            DisplayName = modelKey,
            MatchType = ModelMatchTypes.Exact,
            MatchPattern = modelKey,
            Enabled = true,
            Pricing = new ModelPricingPlanRequest
            {
                Currency = "USD",
                Enabled = true,
                Rules =
                [
                    new ModelPricingRuleRequest
                    {
                        BillingItem = ModelBillingItems.Input,
                        BillingMode = ModelBillingModes.PerMillionTokens,
                        UnitPrice = inputPrice,
                        Enabled = true
                    }
                ]
            }
        };
    }

    private static ModelInfoUpdateRequest BatchModelRequest(string modelKey, bool enabled)
    {
        return new ModelInfoUpdateRequest
        {
            ProviderCode = "batch-test",
            ModelKey = modelKey,
            DisplayName = modelKey,
            MatchType = ModelMatchTypes.Exact,
            MatchPattern = modelKey,
            Enabled = enabled,
            Pricing = new ModelPricingPlanRequest
            {
                Currency = "USD",
                Enabled = true,
                Rules =
                [
                    new ModelPricingRuleRequest
                    {
                        BillingItem = ModelBillingItems.Input,
                        BillingMode = ModelBillingModes.PerMillionTokens,
                        UnitPrice = 1m,
                        Enabled = true
                    }
                ]
            }
        };
    }

    private static readonly Guid TestUserId = Guid.Parse("99999999-9999-9999-9999-999999999901");

    private sealed class TestWorkContext : IWorkContext
    {
        private readonly SessionUser _user;

        public TestWorkContext(Guid userId, string username, string role)
        {
            _user = new SessionUser(userId, username, role, true);
        }

        public SessionUser? CurrentUser => _user;

        public bool IsSignedIn => true;

        public bool IsSuperadmin => _user.Role == "superadmin";

        public SessionUser RequireUser()
        {
            return _user;
        }

        public SessionUser RequireSuperadmin()
        {
            return IsSuperadmin
                ? _user
                : throw new UnauthorizedAccessException("superadmin required");
        }
    }

    private sealed class InMemoryCacheService : ICacheService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

        public async Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T?>> factory, TimeSpan? ttl = null)
        {
            if (_values.TryGetValue(key, out var cached) && cached is T value)
            {
                return value;
            }

            var created = await factory();
            if (created is not null)
            {
                _values[key] = created;
            }

            return created;
        }

        public Task RemoveAsync(string key)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(IEnumerable<string> keys)
        {
            foreach (var key in keys)
            {
                _values.Remove(key);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestRedisConnectionProvider : IRedisConnectionProvider
    {
        private readonly IDatabase _database;

        public TestRedisConnectionProvider(long version)
        {
            Version = version;
            _database = DispatchProxy.Create<IDatabase, TestRedisDatabaseProxy>();
            ((TestRedisDatabaseProxy)(object)_database).Provider = this;
        }

        public bool Available { get; set; } = true;

        public long Version { get; set; }

        public bool IsAvailable => Available;

        public string KeyPrefix => "test";

        public IDatabase? GetDatabase(int db = -1)
        {
            return Available ? _database : null;
        }

        public ISubscriber? GetSubscriber()
        {
            return null;
        }
    }

    private class TestRedisDatabaseProxy : DispatchProxy
    {
        public TestRedisConnectionProvider Provider { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IDatabase.StringGetAsync)
                && targetMethod.ReturnType == typeof(Task<RedisValue>))
            {
                return Task.FromResult((RedisValue)Provider.Version);
            }

            if (targetMethod.Name == nameof(IDatabase.StringIncrement)
                && targetMethod.ReturnType == typeof(long))
            {
                var increment = args?.OfType<long>().FirstOrDefault() ?? 1L;
                Provider.Version += increment;
                return Provider.Version;
            }

            if (targetMethod.Name == nameof(IDatabase.StringIncrementAsync)
                && targetMethod.ReturnType == typeof(Task<long>))
            {
                var increment = args?.OfType<long>().FirstOrDefault() ?? 1L;
                Provider.Version += increment;
                return Task.FromResult(Provider.Version);
            }

            throw new NotSupportedException(targetMethod.ToString());
        }
    }

    private static string CreateDbPath()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-model-catalog-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return dbPath;
    }
}

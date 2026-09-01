using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Api.Tests.Infrastructure;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Caching;
using OpenCodex.CoreBase.Caching;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Results;
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
    public async Task CalculateCostFallsBackToGlobalPricingWhenChannelModelInfoHasNoPricing()
    {
        var dbPath = CreateDbPath();
        var channelId = Guid.NewGuid();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddChannel(context, channelId, "test-channel", "upstream-model");
            AddModel(context, provider.Id, "global-model", ModelMatchTypes.Exact, "upstream-model", 7m);
            context.ChannelModelInfos.Add(new ChannelModelInfo
            {
                Id = Guid.NewGuid(),
                ChannelId = channelId,
                UpstreamModel = "upstream-model",
                ProviderId = provider.Id,
                ModelKey = "channel-model",
                DisplayName = "Channel Model",
                MatchType = ModelMatchTypes.Exact,
                MatchPattern = "upstream-model",
                CatalogJson = "{\"supported_reasoning_levels\":[{\"effort\":\"low\"}]}",
                CapabilitiesJson = "{}",
                Enabled = true,
                Source = "test",
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.SaveChanges();
        }

        var service = CreateService(dbPath, new InMemoryCacheService());
        var result = await service.CalculateCostAsync(
            channelId,
            "request-model",
            "upstream-model",
            Tokens(1_000_000));

        Assert.Equal(7m, result.Cost);
        Assert.Equal("global_model_match", result.Resolution);
        Assert.Equal("global-model", result.ModelKey);
        Assert.Null(result.ChannelModelInfoId);
    }

    [Fact]
    public void BuildProxyModelCatalogUsesChannelCatalogAndOmitsInternalPricing()
    {
        var dbPath = CreateDbPath();
        var channelId = Guid.NewGuid();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddChannel(context, channelId, "DeepSeek Channel", "deepseek-v4-flash");
            var globalModel = AddModel(
                context,
                provider.Id,
                "global-deepseek",
                ModelMatchTypes.Exact,
                "deepseek-v4-flash",
                7m);
            globalModel.DisplayName = "Shared Model";
            globalModel.CatalogJson = """
                {
                  "display_name": "Shared Model",
                  "default_reasoning_level": "low",
                  "supported_reasoning_levels": [
                    { "effort": "low" },
                    { "effort": "medium" },
                    { "effort": "high" },
                    { "effort": "xhigh" }
                  ]
                }
                """;
            globalModel.CapabilitiesJson = "{\"context_window\":128000}";

            context.ChannelModelInfos.Add(new ChannelModelInfo
            {
                Id = Guid.NewGuid(),
                ChannelId = channelId,
                UpstreamModel = "deepseek-v4-flash",
                ProviderId = provider.Id,
                ModelKey = "channel-deepseek",
                DisplayName = "Shared Model",
                MatchType = ModelMatchTypes.Exact,
                MatchPattern = "deepseek-v4-flash",
                CatalogJson = """
                    {
                      "display_name": "Shared Model",
                      "default_reasoning_level": "medium",
                      "supported_reasoning_levels": [
                        { "effort": "low" },
                        { "effort": "high" },
                        { "effort": "max" }
                      ],
                      "context_window": 1000000
                    }
                    """,
                CapabilitiesJson = "{\"context_window\":1000000}",
                Enabled = true,
                Source = "test",
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = service.BuildProxyModelCatalog(
            [
                new ProxyModelCapabilityDto(
                    "deepseek-v4-flash",
                    false,
                    channelId,
                    "DeepSeek Channel",
                    "deepseek-v4-flash")
            ]);

        var model = Assert.Single(result);
        Assert.Equal("Shared Model", model["display_name"]);
        Assert.Equal("low", model["default_reasoning_level"]);
        Assert.Equal(1000000L, model["context_window"]);
        Assert.Equal(1000000L, model["max_context_window"]);
        var levels = Assert.IsType<List<object?>>(model["supported_reasoning_levels"]);
        Assert.Equal(["low", "high", "max"], levels
            .Cast<Dictionary<string, object?>>()
            .Select(level => level["effort"]));

        // 客户端目录不下发定价与内部标识，避免访问 Key 持有者读取计费数据。
        Assert.DoesNotContain("pricing", model.Keys);
        Assert.DoesNotContain("capabilities", model.Keys);
        Assert.DoesNotContain("catalog", model.Keys);
        Assert.DoesNotContain("source", model.Keys);
    }

    [Fact]
    public void BuildProxyModelCatalogPrefixesChannelNameOnlyWhenDisplayNameConflicts()
    {
        var dbPath = CreateDbPath();
        var channelId = Guid.NewGuid();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddChannel(context, channelId, "DeepSeek Channel", "deepseek-v4-flash");
            var globalModel = AddModel(
                context,
                provider.Id,
                "global-deepseek",
                ModelMatchTypes.Exact,
                "deepseek-v4-flash",
                1m);
            globalModel.DisplayName = "Shared Model";
            globalModel.CatalogJson = "{\"display_name\":\"Shared Model\"}";

            var otherModel = AddModel(
                context,
                provider.Id,
                "other-model",
                ModelMatchTypes.Exact,
                "other-model",
                1m);
            otherModel.DisplayName = "Shared Model";
            otherModel.CatalogJson = "{\"display_name\":\"Shared Model\"}";

            context.ChannelModelInfos.Add(new ChannelModelInfo
            {
                Id = Guid.NewGuid(),
                ChannelId = channelId,
                UpstreamModel = "deepseek-v4-flash",
                ProviderId = provider.Id,
                ModelKey = "channel-deepseek",
                DisplayName = "Shared Model",
                MatchType = ModelMatchTypes.Exact,
                MatchPattern = "deepseek-v4-flash",
                CatalogJson = "{\"display_name\":\"Shared Model\"}",
                CapabilitiesJson = "{}",
                Enabled = true,
                Source = "test",
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = service.BuildProxyModelCatalog(
            [
                new ProxyModelCapabilityDto(
                    "deepseek-v4-flash",
                    false,
                    channelId,
                    "DeepSeek Channel",
                    "deepseek-v4-flash"),
                new ProxyModelCapabilityDto("other-model", false, null, "", "other-model")
            ]);

        Assert.Equal(
            ["DeepSeek Channel/Shared Model", "Shared Model"],
            result.Select(model => (string?)model["display_name"]));
    }

    [Fact]
    public void BuildProxyModelCatalogFallsBackToDefaultReasoningLevelsWithoutCatalog()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var service = CreateService(dbPath);
        var result = service.BuildProxyModelCatalog(
            [new ProxyModelCapabilityDto("unmapped-model", false, null, "", "unmapped-model")]);

        var model = Assert.Single(result);
        Assert.Equal("unmapped-model", model["slug"]);
        Assert.Equal("medium", model["default_reasoning_level"]);
        var levels = Assert.IsType<List<object?>>(model["supported_reasoning_levels"]);
        Assert.Equal(["low", "medium", "high", "xhigh"], levels
            .Cast<Dictionary<string, object?>>()
            .Select(level => level["effort"]));
        Assert.Equal(256000L, model["context_window"]);
        Assert.Equal("freeform", model["apply_patch_tool_type"]);
        Assert.Equal("text", model["web_search_tool_type"]);
    }

    [Fact]
    public void BuildProxyModelCatalogEmbedsCodexRequiredContractWithoutCatalog()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var service = CreateService(dbPath);
        var result = service.BuildProxyModelCatalog(
            [new ProxyModelCapabilityDto("unmapped-model", false, null, "", "unmapped-model")]);

        var model = Assert.Single(result);

        // codex 客户端要求的协议字段,缺失会让整份 /v1/models 响应解析失败。
        Assert.Empty(Assert.IsType<List<object?>>(model["experimental_supported_tools"]));
        var tiers = Assert.IsType<List<object?>>(model["service_tiers"]);
        var tier = Assert.IsType<Dictionary<string, object?>>(Assert.Single(tiers));
        Assert.Equal("priority", tier["id"]);

        // 语义校验要求 base_instructions 或 model_messages.instructions_template 至少一个。
        var baseInstructions = Assert.IsType<string>(model["base_instructions"]);
        Assert.StartsWith("You are Codex, a coding agent based on GPT-5.", baseInstructions);
        var modelMessages = Assert.IsType<Dictionary<string, object?>>(model["model_messages"]);
        var template = Assert.IsType<string>(modelMessages["instructions_template"]);
        Assert.StartsWith("You are Codex, a coding agent based on GPT-5.", template);

        // 模态必须是 codex 接受的变体,且不能凭 supports_image 顺带声明 audio/video。
        Assert.Equal(
            new List<object?> { "text" },
            Assert.IsType<List<object?>>(model["input_modalities"]));
    }

    [Fact]
    public void BuildProxyModelCatalogNormalizesDefaultReasoningSummaryFromCatalog()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddModel(context, provider.Id, "test-model", ModelMatchTypes.Exact, "test-model", 1m);
            var model = context.ModelInfos.First(m => m.ModelKey == "test-model");
            model.CatalogJson = """{"default_reasoning_summary":"short"}""";
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = service.BuildProxyModelCatalog(
            [new ProxyModelCapabilityDto("test-model", false, null, "", "test-model")]);

        var m = Assert.Single(result);
        // "short" 不是 codex 合法值(auto/concise/detailed/none),应归一化为 "auto"。
        Assert.Equal("auto", m["default_reasoning_summary"]);
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
    public void ListChannelModelInfos_GlobalModelLookupDoesNotGrowWithModelCount()
    {
        var dbPath = CreateDbPath();
        var channelId = Guid.NewGuid();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddChannel(context, channelId, "many-models", "model-0");
            for (var i = 1; i < 30; i++)
            {
                context.ChannelModelMappings.Add(new ChannelModelMapping
                {
                    ChannelId = channelId,
                    Position = i,
                    RequestModel = $"model-{i}",
                    UpstreamModel = $"model-{i}",
                    Enabled = true,
                    CreatedAt = 1,
                    UpdatedAt = 1
                });
            }

            // 少量全局模型命中，避免命中数量本身影响查询次数。
            AddModel(context, provider.Id, "global-0", ModelMatchTypes.Exact, "model-0", 1m);
            AddModel(context, provider.Id, "global-1", ModelMatchTypes.Exact, "model-1", 1m);
            context.SaveChanges();
        }

        var capture = new SqlCapture();
        using var captureContext = SqlCapture.CreateCapturingContext($"Data Source={dbPath}", capture);
        var service = new ModelCatalogService(
            new EfRepository<ModelProvider>(captureContext),
            new EfRepository<ModelInfo>(captureContext),
            new EfRepository<ChannelModelInfo>(captureContext),
            new EfRepository<ModelPricingPlan>(captureContext),
            new EfRepository<ModelPricingRule>(captureContext),
            new EfRepository<ChannelModelMapping>(captureContext),
            new EfRepository<Channel>(captureContext),
            new TestWorkContext(TestUserId, "admin", "superadmin"),
            new TestCacheService());

        capture.Reset();
        var listed = service.ListChannelModelInfos(channelId);

        Assert.True(listed.Succeeded);
        Assert.Equal(30, listed.Payload!.Models.Count);
        // 全局模型匹配不应随 upstream model 数量线性增长：修复后应只触发一次
        // ModelInfos 全表加载（跨全部 enabled 全局模型），否则每个 upstream model
        // 都会各查一次 ModelInfos + ModelProviders。
        Assert.True(
            capture.CountMatching("FROM \"ModelInfos\"") <= 2,
            $"ModelInfos 查询次数 {capture.CountMatching("FROM \"ModelInfos\"")} 应随模型数保持恒定");
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
        Assert.Equal(2, exported.Payload.Version);
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
            ("unknown version", MutateImport(payload => payload.Version = 3)),
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

    [Fact]
    public async Task OffPeakWindowSwitchesUnitPrice()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddOffPeakModel(
                context,
                provider.Id,
                "night-model",
                "UTC",
                [Window("22:00", "24:00")],
                peakUnitPrice: 1m,
                offPeakUnitPrice: 0.5m);
        }

        var service = CreateService(dbPath);

        var offPeak = await service.CalculateCostAsync(
            null,
            null,
            "night-model",
            Tokens(1_000_000),
            Utc(2026, 1, 5, 22, 30));
        var peak = await service.CalculateCostAsync(
            null,
            null,
            "night-model",
            Tokens(1_000_000),
            Utc(2026, 1, 5, 21, 30));

        Assert.Equal(0.5m, offPeak.Cost);
        Assert.Equal(PricingPhases.OffPeak, offPeak.PricingPhase);
        Assert.Equal(PricingPhaseSources.WindowHit, offPeak.PhaseSource);
        Assert.Equal(1m, peak.Cost);
        Assert.Equal(PricingPhases.Peak, peak.PricingPhase);
        Assert.Equal(PricingPhaseSources.WindowMiss, peak.PhaseSource);
    }

    [Fact]
    public async Task OffPeakWindowUsesHalfOpenBoundaries()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddOffPeakModel(
                context,
                provider.Id,
                "edge-model",
                "UTC",
                [Window("22:00", "23:00")],
                peakUnitPrice: 1m,
                offPeakUnitPrice: 0.5m);
        }

        var service = CreateService(dbPath);

        Assert.Equal(0.5m, (await service.CalculateCostAsync(
            null, null, "edge-model", Tokens(1_000_000), Utc(2026, 1, 5, 22, 0))).Cost);
        Assert.Equal(0.5m, (await service.CalculateCostAsync(
            null, null, "edge-model", Tokens(1_000_000), Utc(2026, 1, 5, 22, 59))).Cost);
        Assert.Equal(1m, (await service.CalculateCostAsync(
            null, null, "edge-model", Tokens(1_000_000), Utc(2026, 1, 5, 23, 0))).Cost);
        Assert.Equal(1m, (await service.CalculateCostAsync(
            null, null, "edge-model", Tokens(1_000_000), Utc(2026, 1, 5, 21, 59))).Cost);
    }

    [Fact]
    public async Task CrossMidnightWindowFollowsStartDayWeekdays()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            // 只在周一晚间起算的跨午夜窗口:规范化后为周一 22:00-24:00 与周二 00:00-06:00。
            AddOffPeakModel(
                context,
                provider.Id,
                "cross-model",
                "UTC",
                [Window("22:00", "06:00", 1)],
                peakUnitPrice: 1m,
                offPeakUnitPrice: 0.5m);
        }

        var service = CreateService(dbPath);

        // 2026-01-05 是周一,2026-01-06 是周二。
        Assert.Equal(0.5m, (await service.CalculateCostAsync(
            null, null, "cross-model", Tokens(1_000_000), Utc(2026, 1, 5, 22, 30))).Cost);
        Assert.Equal(0.5m, (await service.CalculateCostAsync(
            null, null, "cross-model", Tokens(1_000_000), Utc(2026, 1, 6, 1, 0))).Cost);
        Assert.Equal(1m, (await service.CalculateCostAsync(
            null, null, "cross-model", Tokens(1_000_000), Utc(2026, 1, 5, 1, 0))).Cost);
        Assert.Equal(1m, (await service.CalculateCostAsync(
            null, null, "cross-model", Tokens(1_000_000), Utc(2026, 1, 6, 22, 30))).Cost);
    }

    [Fact]
    public async Task TimeZoneDecidesPricingPhase()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddOffPeakModel(
                context,
                provider.Id,
                "utc-model",
                "UTC",
                [Window("22:00", "24:00")],
                peakUnitPrice: 1m,
                offPeakUnitPrice: 0.5m);
            AddOffPeakModel(
                context,
                provider.Id,
                "shanghai-model",
                "Asia/Shanghai",
                [Window("22:00", "24:00")],
                peakUnitPrice: 1m,
                offPeakUnitPrice: 0.5m);
        }

        var service = CreateService(dbPath);
        // 14:30 UTC 就是 22:30 Asia/Shanghai。
        var instant = Utc(2026, 1, 5, 14, 30);

        Assert.Equal(1m, (await service.CalculateCostAsync(
            null, null, "utc-model", Tokens(1_000_000), instant)).Cost);
        Assert.Equal(0.5m, (await service.CalculateCostAsync(
            null, null, "shanghai-model", Tokens(1_000_000), instant)).Cost);
    }

    [Fact]
    public async Task RuleWithoutOffPeakKeepsBasePriceInOffPeakWindow()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddOffPeakModel(
                context,
                provider.Id,
                "flat-model",
                "UTC",
                [Window("22:00", "24:00")],
                peakUnitPrice: 1m,
                offPeakUnitPrice: 0.5m,
                offPeakEnabled: false);
        }

        var service = CreateService(dbPath);
        var result = await service.CalculateCostAsync(
            null,
            null,
            "flat-model",
            Tokens(1_000_000),
            Utc(2026, 1, 5, 22, 30));

        Assert.Equal(1m, result.Cost);
        Assert.Equal(PricingPhases.OffPeak, result.PricingPhase);
        using var snapshot = JsonDocument.Parse(result.SnapshotJson);
        Assert.Equal(
            PricingPhases.Peak,
            snapshot.RootElement.GetProperty("rules")[0].GetProperty("applied_phase").GetString());
    }

    [Fact]
    // 阶梯 token 现按上下文窗口档位计费：用 InputTokens 选档，整段按该档单价计费，不再分段累乘。
    public async Task TieredOffPeakUsesOffPeakTiers()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            var model = AddOffPeakModel(
                context,
                provider.Id,
                "tier-model",
                "UTC",
                [Window("22:00", "24:00")],
                peakUnitPrice: 0m,
                offPeakUnitPrice: 0m);
            var plan = context.ModelPricingPlans.Single(item => item.ModelInfoId == model.Id);
            var rule = context.ModelPricingRules.Single(item => item.PricingPlanId == plan.Id);
            rule.BillingMode = ModelBillingModes.TieredTokens;
            rule.TiersJson = """[{"up_to":500000,"unit_price":2},{"up_to":null,"unit_price":1}]""";
            rule.OffPeakTiersJson = """[{"up_to":500000,"unit_price":1},{"up_to":null,"unit_price":0.5}]""";
            context.SaveChanges();
        }

        var service = CreateService(dbPath);

        // InputTokens=1_000_000 落在 up_to:null 兜底档：峰段单价 1、谷段单价 0.5，整段 100 万 token。
        Assert.Equal(1m, (await service.CalculateCostAsync(
            null, null, "tier-model", Tokens(1_000_000), Utc(2026, 1, 5, 21, 0))).Cost);
        Assert.Equal(0.5m, (await service.CalculateCostAsync(
            null, null, "tier-model", Tokens(1_000_000), Utc(2026, 1, 5, 22, 30))).Cost);
    }

    [Fact]
    public async Task TieredTokensSelectsTierByContextWindow()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            var model = AddModel(
                context,
                provider.Id,
                "ctx-tier-model",
                ModelMatchTypes.Exact,
                "ctx-tier-model",
                inputPrice: 0m);
            var plan = context.ModelPricingPlans.Single(item => item.ModelInfoId == model.Id);
            var rule = context.ModelPricingRules.Single(item => item.PricingPlanId == plan.Id);
            rule.BillingMode = ModelBillingModes.TieredTokens;
            // 上限 32000：窗口 <32K 落第一档单价 6，>=32K 落兜底档单价 8。
            rule.TiersJson = """[{"up_to":32000,"unit_price":6},{"up_to":null,"unit_price":8}]""";
            context.SaveChanges();
        }

        var service = CreateService(dbPath);

        // 窗口 20000 < 32000：选第一档，整段按 6 计费：20000 * 6 / 1_000_000 = 0.12
        Assert.Equal(0.12m, (await service.CalculateCostAsync(
            null, null, "ctx-tier-model", Tokens(20_000))).Cost);
        // 窗口 32000 命中上限边界：第一档 up_to=32000 >= 32000，选第一档，整段按 6 计费。
        Assert.Equal(0.192m, (await service.CalculateCostAsync(
            null, null, "ctx-tier-model", Tokens(32_000))).Cost);
        // 窗口 50000 > 32000：选兜底档，整段按 8 计费：50000 * 8 / 1_000_000 = 0.4
        Assert.Equal(0.4m, (await service.CalculateCostAsync(
            null, null, "ctx-tier-model", Tokens(50_000))).Cost);
    }

    [Fact]
    public async Task TieredTokensShareInputContextWindowAcrossBillingItems()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            var model = AddModel(
                context,
                provider.Id,
                "ctx-shared-model",
                ModelMatchTypes.Exact,
                "ctx-shared-model",
                inputPrice: 0m);
            var plan = context.ModelPricingPlans.Single(item => item.ModelInfoId == model.Id);
            context.ModelPricingRules.RemoveRange(context.ModelPricingRules.Where(item => item.PricingPlanId == plan.Id));
            var inputRule = Rule(plan.Id, ModelBillingItems.Input, 0m);
            inputRule.BillingMode = ModelBillingModes.TieredTokens;
            inputRule.TiersJson = """[{"up_to":32000,"unit_price":6},{"up_to":null,"unit_price":8}]""";
            var outputRule = Rule(plan.Id, ModelBillingItems.Output, 0m);
            outputRule.BillingMode = ModelBillingModes.TieredTokens;
            outputRule.TiersJson = """[{"up_to":32000,"unit_price":6},{"up_to":null,"unit_price":8}]""";
            context.ModelPricingRules.AddRange(inputRule, outputRule);
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        // input 20000 落第一档（单价 6）；output 50000 若按自己的 token 选档会落兜底档（单价 8），
        // 但窗口档语义要求所有计费项都用 InputTokens 选档，因此 output 仍应取单价 6。
        // input 20000 * 6 / 1M = 0.12，output 50000 * 6 / 1M = 0.30，合计 0.42。
        var result = await service.CalculateCostAsync(
            null,
            null,
            "ctx-shared-model",
            new ModelUsageVector(inputTokens: 20_000, outputTokens: 50_000, cacheWriteTokens: 0, cacheReadTokens: 0));

        Assert.Equal(0.42m, result.Cost);
    }

    [Fact]
    public async Task PerRequestOffPeakSwitchesUnitPrice()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            var model = AddOffPeakModel(
                context,
                provider.Id,
                "per-request-model",
                "UTC",
                [Window("22:00", "24:00")],
                peakUnitPrice: 0.02m,
                offPeakUnitPrice: 0.01m);
            var plan = context.ModelPricingPlans.Single(item => item.ModelInfoId == model.Id);
            var rule = context.ModelPricingRules.Single(item => item.PricingPlanId == plan.Id);
            rule.BillingMode = ModelBillingModes.PerRequest;
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var usage = new ModelUsageVector(0, 0, 0, 0);

        Assert.Equal(0.02m, (await service.CalculateCostAsync(
            null, null, "per-request-model", usage, Utc(2026, 1, 5, 21, 0))).Cost);
        Assert.Equal(0.01m, (await service.CalculateCostAsync(
            null, null, "per-request-model", usage, Utc(2026, 1, 5, 22, 30))).Cost);
    }

    [Fact]
    public async Task PricingCacheDoesNotFreezePricingPhase()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddOffPeakModel(
                context,
                provider.Id,
                "cached-phase-model",
                "UTC",
                [Window("22:00", "24:00")],
                peakUnitPrice: 1m,
                offPeakUnitPrice: 0.5m);
        }

        // 同一个缓存实例、同一个 (channelId, upstreamModel):定价解析会命中缓存,
        // 但时段必须每次现算,否则跨越窗口边界的请求会按旧时段计费。
        var service = CreateService(dbPath, new InMemoryCacheService());

        var peak = await service.CalculateCostAsync(
            null, null, "cached-phase-model", Tokens(1_000_000), Utc(2026, 1, 5, 21, 59));
        var offPeak = await service.CalculateCostAsync(
            null, null, "cached-phase-model", Tokens(1_000_000), Utc(2026, 1, 5, 22, 0));

        Assert.Equal(1m, peak.Cost);
        Assert.Equal(0.5m, offPeak.Cost);
    }

    [Fact]
    public async Task UnresolvableTimeZoneFallsBackToPeakPrice()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            var model = AddOffPeakModel(
                context,
                provider.Id,
                "bad-zone-model",
                "UTC",
                [Window("00:00", "24:00")],
                peakUnitPrice: 1m,
                offPeakUnitPrice: 0.5m);
            var plan = context.ModelPricingPlans.Single(item => item.ModelInfoId == model.Id);
            // 绕过写入校验模拟运行环境缺少时区数据的情况。
            plan.TimeZoneId = "Not/AZone";
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = await service.CalculateCostAsync(
            null,
            null,
            "bad-zone-model",
            Tokens(1_000_000),
            Utc(2026, 1, 5, 12, 0));

        Assert.Equal(1m, result.Cost);
        Assert.Equal(PricingPhases.Peak, result.PricingPhase);
        Assert.Equal(PricingPhaseSources.TimeZoneUnresolved, result.PhaseSource);
    }

    [Fact]
    public async Task PricingSnapshotRecordsPhaseDetails()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddOffPeakModel(
                context,
                provider.Id,
                "snapshot-model",
                "Asia/Shanghai",
                [Window("22:00", "24:00")],
                peakUnitPrice: 1m,
                offPeakUnitPrice: 0.5m);
        }

        var service = CreateService(dbPath);
        var instant = Utc(2026, 1, 5, 14, 30);
        var result = await service.CalculateCostAsync(
            null,
            null,
            "snapshot-model",
            Tokens(1_000_000),
            instant);

        using var snapshot = JsonDocument.Parse(result.SnapshotJson);
        var root = snapshot.RootElement;
        Assert.Equal(PricingPhases.OffPeak, root.GetProperty("pricing_phase").GetString());
        Assert.Equal(PricingPhaseSources.WindowHit, root.GetProperty("phase_source").GetString());
        Assert.Equal("Asia/Shanghai", root.GetProperty("time_zone").GetString());
        Assert.Equal(
            instant.ToUnixTimeMilliseconds() / 1000.0,
            root.GetProperty("billing_instant").GetDouble());
        Assert.Equal("22:00", root.GetProperty("matched_window").GetProperty("start").GetString());
        Assert.Equal("24:00", root.GetProperty("matched_window").GetProperty("end").GetString());
        var ruleSnapshot = root.GetProperty("rules")[0];
        Assert.Equal(PricingPhases.OffPeak, ruleSnapshot.GetProperty("applied_phase").GetString());
        Assert.Equal(0.5m, ruleSnapshot.GetProperty("unit_price").GetDecimal());
    }

    [Fact]
    public async Task PlanWithoutPeakOffPeakKeepsLegacyCost()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddModel(context, provider.Id, "legacy", ModelMatchTypes.Exact, "legacy-model", 3m);
        }

        var service = CreateService(dbPath);
        var result = await service.CalculateCostAsync(
            null,
            null,
            "legacy-model",
            Tokens(1_000_000),
            Utc(2026, 1, 5, 22, 30));

        Assert.Equal(3m, result.Cost);
        Assert.Equal(PricingPhases.Peak, result.PricingPhase);
        Assert.Equal(PricingPhaseSources.Disabled, result.PhaseSource);
    }

    [Fact]
    public void CreateModelNormalizesCrossMidnightOffPeakWindows()
    {
        var service = CreatePeakOffPeakService();
        var created = service.CreateModel(
            OffPeakModelRequest("night-model", "Asia/Shanghai", [Window("22:00", "06:00", 1)]));

        Assert.True(created.Succeeded);
        var pricing = created.Payload!.Model.Pricing;
        Assert.NotNull(pricing);
        Assert.Equal("Asia/Shanghai", pricing!.TimeZone);
        Assert.Collection(
            pricing.OffPeakWindows,
            first =>
            {
                Assert.Equal("00:00", first.Start);
                Assert.Equal("06:00", first.End);
                Assert.Equal(new[] { 2 }, first.Days);
            },
            second =>
            {
                Assert.Equal("22:00", second.Start);
                Assert.Equal("24:00", second.End);
                Assert.Equal(new[] { 1 }, second.Days);
            });
        var rule = pricing.Rules.Single(item => item.BillingItem == ModelBillingItems.Input);
        Assert.True(rule.OffPeakEnabled);
        Assert.Equal(0.5m, rule.OffPeakUnitPrice);
    }

    [Fact]
    public void PeakOffPeakConfigRoundTripIsStable()
    {
        var service = CreatePeakOffPeakService();
        var created = service.CreateModel(
            OffPeakModelRequest("stable-model", "UTC", [Window("22:00", "06:00", 5)]));
        Assert.True(created.Succeeded);
        var first = created.Payload!.Model.Pricing!;

        // 把读回的规范化结果原样再提交,结果必须稳定,否则每次保存都会漂移。
        var resubmitted = service.UpdateModel(
            created.Payload.Model.Id,
            OffPeakModelRequest("stable-model", "UTC", first.OffPeakWindows));

        Assert.True(resubmitted.Succeeded);
        var second = resubmitted.Payload!.Model.Pricing!;
        Assert.Equal(
            first.OffPeakWindows.Select(window => (window.Start, window.End, string.Join(",", window.Days))),
            second.OffPeakWindows.Select(window => (window.Start, window.End, string.Join(",", window.Days))));
    }

    [Fact]
    public void InvalidPeakOffPeakConfigIsRejected()
    {
        var service = CreatePeakOffPeakService();

        AssertBadRequest(service.CreateModel(
            OffPeakModelRequest("bad-zone", "Not/AZone", [Window("22:00", "24:00")])));
        AssertBadRequest(service.CreateModel(
            OffPeakModelRequest("bad-time", "UTC", [Window("2200", "24:00")])));
        AssertBadRequest(service.CreateModel(
            OffPeakModelRequest("bad-minute", "UTC", [Window("22:70", "24:00")])));
        AssertBadRequest(service.CreateModel(
            OffPeakModelRequest("start-as-end-of-day", "UTC", [Window("24:00", "06:00")])));
        AssertBadRequest(service.CreateModel(
            OffPeakModelRequest("same-time", "UTC", [Window("22:00", "22:00")])));
        AssertBadRequest(service.CreateModel(
            OffPeakModelRequest("bad-day", "UTC", [Window("22:00", "24:00", 8)])));
        AssertBadRequest(service.CreateModel(OffPeakModelRequest(
            "too-many-windows",
            "UTC",
            Enumerable.Range(0, PricingWindowCalendar.MaxWindows + 1)
                .Select(index => Window("01:00", "02:00", (index % 7) + 1))
                .ToList())));

        // 校验必须发生在写库之前,否则会留下没有价格计划的模型。
        Assert.Empty(service.ListModels(null, null, null).Payload!.Models);
    }

    [Fact]
    public void OffPeakTieredRuleWithoutOffPeakTiersIsRejected()
    {
        var service = CreatePeakOffPeakService();
        var request = OffPeakModelRequest("tier-guard", "UTC", [Window("22:00", "24:00")]);
        request.Pricing!.Rules[0].BillingMode = ModelBillingModes.TieredTokens;
        request.Pricing.Rules[0].Tiers = [new ModelPricingTierRequest { UpTo = null, UnitPrice = 1m }];
        request.Pricing.Rules[0].OffPeakTiers = [];

        AssertBadRequest(service.CreateModel(request));
    }

    [Fact]
    public void TieredRuleMustHaveAtLeastOneTier()
    {
        var service = CreatePeakOffPeakService();
        var request = OffPeakModelRequest("tier-empty", "UTC", [Window("22:00", "24:00")]);
        request.Pricing!.Rules[0].BillingMode = ModelBillingModes.TieredTokens;
        request.Pricing.Rules[0].Tiers = [];
        request.Pricing.Rules[0].OffPeakTiers = [new ModelPricingTierRequest { UpTo = null, UnitPrice = 1m }];

        AssertBadRequest(service.CreateModel(request));
        Assert.Empty(service.ListModels(null, null, null).Payload!.Models);
    }

    [Fact]
    public void TieredRuleRejectsMultipleUnlimitedTiers()
    {
        var service = CreatePeakOffPeakService();
        var request = OffPeakModelRequest("tier-multi-null", "UTC", [Window("22:00", "24:00")]);
        request.Pricing!.Rules[0].BillingMode = ModelBillingModes.TieredTokens;
        request.Pricing.Rules[0].Tiers =
        [
            new ModelPricingTierRequest { UpTo = null, UnitPrice = 1m },
            new ModelPricingTierRequest { UpTo = null, UnitPrice = 2m }
        ];
        request.Pricing.Rules[0].OffPeakTiers = [new ModelPricingTierRequest { UpTo = null, UnitPrice = 1m }];

        AssertBadRequest(service.CreateModel(request));
        Assert.Empty(service.ListModels(null, null, null).Payload!.Models);
    }

    [Fact]
    public void TieredRuleRejectsZeroUpTo()
    {
        var service = CreatePeakOffPeakService();
        var request = OffPeakModelRequest("tier-zero-up", "UTC", [Window("22:00", "24:00")]);
        request.Pricing!.Rules[0].BillingMode = ModelBillingModes.TieredTokens;
        request.Pricing.Rules[0].Tiers =
        [
            new ModelPricingTierRequest { UpTo = 0, UnitPrice = 1m },
            new ModelPricingTierRequest { UpTo = null, UnitPrice = 2m }
        ];
        request.Pricing.Rules[0].OffPeakTiers = [new ModelPricingTierRequest { UpTo = null, UnitPrice = 1m }];

        AssertBadRequest(service.CreateModel(request));
        Assert.Empty(service.ListModels(null, null, null).Payload!.Models);
    }

    [Fact]
    public async Task ConfiguredPeakOffPeakPlanChangesBilledCost()
    {
        var service = CreatePeakOffPeakService();
        // 管理台配置:上海时区,周一 22:00 起的跨午夜谷段。
        Assert.True(service.CreateModel(
            OffPeakModelRequest("e2e-model", "Asia/Shanghai", [Window("22:00", "06:00", 1)]))
            .Succeeded);

        // 周一 22:30 上海 = 14:30 UTC
        Assert.Equal(0.5m, (await service.CalculateCostAsync(
            null, null, "e2e-model", Tokens(1_000_000), Utc(2026, 1, 5, 14, 30))).Cost);
        // 周二 01:00 上海 = 周一 17:00 UTC,属于周一晚间那一段
        Assert.Equal(0.5m, (await service.CalculateCostAsync(
            null, null, "e2e-model", Tokens(1_000_000), Utc(2026, 1, 5, 17, 0))).Cost);
        // 周二 07:00 上海 = 周一 23:00 UTC,已出谷段
        Assert.Equal(1m, (await service.CalculateCostAsync(
            null, null, "e2e-model", Tokens(1_000_000), Utc(2026, 1, 5, 23, 0))).Cost);
        // 周二 22:30 上海 = 周二 14:30 UTC,周二晚间未选中
        Assert.Equal(1m, (await service.CalculateCostAsync(
            null, null, "e2e-model", Tokens(1_000_000), Utc(2026, 1, 6, 14, 30))).Cost);
    }

    [Fact]
    public void ExportImportRoundTripPreservesPeakOffPeakAndReportsUnchanged()
    {
        var service = CreatePeakOffPeakService();
        Assert.True(service.CreateModel(
            OffPeakModelRequest("round-trip", "Asia/Shanghai", [Window("22:00", "24:00", 1, 2, 3, 4, 5)]))
            .Succeeded);

        var exported = service.ExportModelCatalog();
        Assert.True(exported.Succeeded);
        Assert.Equal(2, exported.Payload!.Version);
        var pricing = exported.Payload.Models.Single().Pricing!;
        Assert.Equal("Asia/Shanghai", pricing.TimeZone);
        Assert.Equal("22:00", pricing.OffPeakWindows.Single().Start);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, pricing.OffPeakWindows.Single().Days);
        Assert.Equal(0.5m, pricing.Rules.Single(rule => rule.BillingItem == ModelBillingItems.Input).OffPeakUnitPrice);

        var reimported = service.ImportModelCatalog(exported.Payload, dryRun: false);
        Assert.True(reimported.Succeeded);
        Assert.Equal(1, reimported.Payload!.Models.Unchanged);
        Assert.Equal(0, reimported.Payload.Models.Updated);
    }

    [Fact]
    public void ImportVersion1DocumentWithoutPeakOffPeakSucceeds()
    {
        var service = CreatePeakOffPeakService();
        var document = new ModelCatalogTransferDocument
        {
            Type = "model_catalog",
            Version = 1,
            Providers =
            [
                new ModelCatalogProviderTransfer { Code = "peak-test", Name = "Peak Test", Enabled = true }
            ],
            Models =
            [
                new ModelCatalogModelTransfer
                {
                    ProviderCode = "peak-test",
                    ModelKey = "legacy-doc",
                    DisplayName = "legacy-doc",
                    MatchType = ModelMatchTypes.Exact,
                    MatchPattern = "legacy-doc",
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

        var result = service.ImportModelCatalog(document, dryRun: false);

        Assert.True(result.Succeeded);
        var model = service.ListModels(null, null, null).Payload!.Models
            .Single(item => item.ModelKey == "legacy-doc");
        Assert.Equal(string.Empty, model.Pricing!.TimeZone);
        Assert.Empty(model.Pricing.OffPeakWindows);
        Assert.False(model.Pricing.Rules.Single().OffPeakEnabled);
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

    private static PricingOffPeakWindow Window(string start, string end, params int[] days)
    {
        return new PricingOffPeakWindow
        {
            Start = start,
            End = end,
            Days = days.ToList()
        };
    }

    private static void AssertBadRequest<T>(ApiOpResult<T> result)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Code);
    }

    private static ModelCatalogService CreatePeakOffPeakService()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var service = CreateService(dbPath);
        var provider = service.CreateProvider(new ModelProviderUpsertRequest
        {
            Code = "peak-test",
            Name = "Peak Test",
            Enabled = true
        });
        Assert.True(provider.Succeeded);
        return service;
    }

    private static ModelInfoUpdateRequest OffPeakModelRequest(
        string modelKey,
        string timeZone,
        IEnumerable<PricingOffPeakWindow> windows)
    {
        return new ModelInfoUpdateRequest
        {
            ProviderCode = "peak-test",
            ModelKey = modelKey,
            DisplayName = modelKey,
            MatchType = ModelMatchTypes.Exact,
            MatchPattern = modelKey,
            Enabled = true,
            Pricing = new ModelPricingPlanRequest
            {
                Currency = "USD",
                Enabled = true,
                TimeZone = timeZone,
                OffPeakWindows = windows.ToList(),
                Rules =
                [
                    new ModelPricingRuleRequest
                    {
                        BillingItem = ModelBillingItems.Input,
                        BillingMode = ModelBillingModes.PerMillionTokens,
                        UnitPrice = 1m,
                        OffPeakEnabled = true,
                        OffPeakUnitPrice = 0.5m,
                        Enabled = true
                    }
                ]
            }
        };
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute)
    {
        return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
    }

    private static ModelInfo AddOffPeakModel(
        IOpenCodexDbContext context,
        Guid providerId,
        string modelKey,
        string timeZoneId,
        IEnumerable<PricingOffPeakWindow> windows,
        decimal peakUnitPrice,
        decimal offPeakUnitPrice,
        bool offPeakEnabled = true)
    {
        var model = new ModelInfo
        {
            Scope = ModelInfoScopes.Global,
            ProviderId = providerId,
            ChannelId = null,
            ModelKey = modelKey,
            DisplayName = modelKey,
            Description = string.Empty,
            MatchType = ModelMatchTypes.Exact,
            MatchPattern = modelKey,
            CatalogJson = "{}",
            CapabilitiesJson = "{}",
            Enabled = true,
            Source = "test",
            CreatedAt = 1,
            UpdatedAt = 1
        };
        context.ModelInfos.Add(model);
        context.SaveChanges();

        var plan = new ModelPricingPlan
        {
            ModelInfoId = model.Id,
            ChannelId = null,
            Currency = "USD",
            TimeZoneId = timeZoneId,
            OffPeakWindowsJson = PricingWindowCalendar.Serialize(windows),
            Enabled = true,
            Source = "test",
            CreatedAt = 1,
            UpdatedAt = 1
        };
        context.ModelPricingPlans.Add(plan);
        context.SaveChanges();

        var rule = Rule(plan.Id, ModelBillingItems.Input, peakUnitPrice);
        rule.OffPeakEnabled = offPeakEnabled;
        rule.OffPeakUnitPrice = offPeakUnitPrice;
        context.ModelPricingRules.Add(rule);
        context.SaveChanges();
        return model;
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

    // --- Sync mode tests (batch 1) ---

    private static ModelCatalogImportOptions IncrementalSyncOptions() => new()
    {
        SkipExistingModels = true,
        SkipExistingProviders = true,
        PreserveLocalEnabled = true,
        KeepLocalPricingWhenRemoteNull = true,
        Source = ModelCatalogSources.Sync
    };

    private static ModelCatalogImportOptions OverwriteSyncOptions() => new()
    {
        SkipExistingModels = false,
        SkipExistingProviders = true,
        PreserveLocalEnabled = true,
        KeepLocalPricingWhenRemoteNull = true,
        Source = ModelCatalogSources.Sync
    };

    private static ModelCatalogTransferDocument SyncPayloadWithNewAndExisting()
    {
        return new ModelCatalogTransferDocument
        {
            Type = "model_catalog",
            Version = 1,
            ExportedAt = "2026-08-27T02:00:00Z",
            Providers =
            [
                new ModelCatalogProviderTransfer
                {
                    Code = "test",
                    Name = "Test Renamed",
                    Enabled = false,
                    SortOrder = 99
                },
                new ModelCatalogProviderTransfer
                {
                    Code = "new-sync-provider",
                    Name = "New Sync Provider",
                    Enabled = true,
                    SortOrder = 50
                }
            ],
            Models =
            [
                new ModelCatalogModelTransfer
                {
                    ProviderCode = "test",
                    ModelKey = "existing",
                    DisplayName = "Existing Renamed",
                    Description = "remote desc",
                    MatchType = ModelMatchTypes.Exact,
                    MatchPattern = "existing",
                    Catalog = [],
                    Capabilities = [],
                    Enabled = false,
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
                                UnitPrice = 9m,
                                Enabled = true
                            }
                        ]
                    }
                },
                new ModelCatalogModelTransfer
                {
                    ProviderCode = "new-sync-provider",
                    ModelKey = "sync-new-model",
                    DisplayName = "Sync New Model",
                    Description = string.Empty,
                    MatchType = ModelMatchTypes.Exact,
                    MatchPattern = "sync-new-model",
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
                                UnitPrice = 2m,
                                Enabled = true
                            }
                        ]
                    }
                }
            ]
        };
    }

    [Fact]
    public void SyncIncrementalCreatesNewModelsAndSkipsExisting()
    {
        var dbPath = CreateDbPath();
        Guid existingModelId;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context, "test", "Test", ModelCatalogSources.Manual, 1);
            var model = AddModel(context, provider.Id, "existing", ModelMatchTypes.Exact, "existing", 1m);
            existingModelId = model.Id;
        }

        var service = CreateService(dbPath);
        var document = SyncPayloadWithNewAndExisting();
        var dryRun = service.ImportModelCatalog(document, dryRun: true, IncrementalSyncOptions());

        Assert.True(dryRun.Succeeded);
        Assert.True(dryRun.Payload!.DryRun);
        Assert.Equal(1, dryRun.Payload.Models.Created);
        Assert.Equal(1, dryRun.Payload.Skipped);
        Assert.Single(dryRun.Payload.CreatedModelKeys, "sync-new-model");
        Assert.Single(dryRun.Payload.SkippedModelKeys, "existing");
        Assert.Empty(dryRun.Payload.OverwrittenModelKeys);
        Assert.Equal(0, dryRun.Payload.PricingDeleted);

        // dry run: no DB changes
        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            Assert.DoesNotContain(verify.ModelInfos, item => item.ModelKey == "sync-new-model");
        }

        var imported = service.ImportModelCatalog(document, dryRun: false, IncrementalSyncOptions());
        Assert.True(imported.Succeeded);
        Assert.False(imported.Payload!.DryRun);
        Assert.Equal(1, imported.Payload.Models.Created);
        Assert.Equal(1, imported.Payload.Skipped);

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            // New model created with source=sync
            var newModel = verify.ModelInfos.Single(item => item.ModelKey == "sync-new-model");
            Assert.Equal(ModelCatalogSources.Sync, newModel.Source);
            var newPlan = verify.ModelPricingPlans.Single(item => item.ModelInfoId == newModel.Id);
            Assert.Equal(2m, verify.ModelPricingRules
                .Single(rule => rule.PricingPlanId == newPlan.Id).UnitPrice);

            // Existing model untouched: name, price, enabled, source all preserved
            var existing = verify.ModelInfos.Single(item => item.Id == existingModelId);
            Assert.Equal("existing", existing.DisplayName);
            Assert.True(existing.Enabled);
            Assert.Equal("test", existing.Source); // AddProvider helper uses "test"
            var existingPlan = verify.ModelPricingPlans.Single(item => item.ModelInfoId == existingModelId);
            Assert.Equal(1m, verify.ModelPricingRules
                .Single(rule => rule.PricingPlanId == existingPlan.Id).UnitPrice);
        }
    }

    [Fact]
    public void SyncIncrementalSkipsDisabledExistingModel()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context, "test", "Test", ModelCatalogSources.Manual, 1);
            var model = AddModel(context, provider.Id, "existing", ModelMatchTypes.Exact, "existing", 1m);
            model.Enabled = false;
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = service.ImportModelCatalog(
            SyncPayloadWithNewAndExisting(), dryRun: false, IncrementalSyncOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Payload!.Skipped);
        Assert.Single(result.Payload.SkippedModelKeys, "existing");

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var model = verify.ModelInfos.Single(item => item.ModelKey == "existing");
            Assert.False(model.Enabled); // still disabled, not revived (Q13)
        }
    }

    [Fact]
    public void SyncIncrementalDoesNotModifyExistingProviders()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            AddProvider(context, "test", "Test", ModelCatalogSources.Manual, 1);
        }

        var service = CreateService(dbPath);
        var result = service.ImportModelCatalog(
            SyncPayloadWithNewAndExisting(), dryRun: false, IncrementalSyncOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Payload!.Providers.Created); // new-sync-provider
        Assert.Equal(0, result.Payload.Providers.Updated);

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var provider = verify.ModelProviders.Single(item => item.Code == "test");
            Assert.Equal("Test", provider.Name);
            Assert.True(provider.Enabled);
            Assert.Equal(1, provider.SortOrder);
            Assert.Equal(ModelCatalogSources.Manual, provider.Source);

            var newProvider = verify.ModelProviders.Single(item => item.Code == "new-sync-provider");
            Assert.Equal(ModelCatalogSources.Sync, newProvider.Source);
        }
    }

    [Fact]
    public void SyncOverwriteUpdatesExistingModelMetadataAndPricing()
    {
        var dbPath = CreateDbPath();
        Guid existingModelId;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context, "test", "Test", ModelCatalogSources.Manual, 1);
            var model = AddModel(context, provider.Id, "existing", ModelMatchTypes.Exact, "existing", 1m);
            existingModelId = model.Id;
        }

        var service = CreateService(dbPath);
        var result = service.ImportModelCatalog(
            SyncPayloadWithNewAndExisting(), dryRun: false, OverwriteSyncOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Payload!.Models.Created);
        Assert.Equal(0, result.Payload.Skipped);
        Assert.Single(result.Payload.OverwrittenModelKeys, "existing");
        Assert.Equal(0, result.Payload.PricingDeleted);

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var existing = verify.ModelInfos.Single(item => item.Id == existingModelId);
            Assert.Equal("Existing Renamed", existing.DisplayName);
            Assert.Equal("remote desc", existing.Description);
            Assert.Equal(ModelCatalogSources.Sync, existing.Source);
            // PreserveLocalEnabled: remote Enabled=false but local was true -> stays true (Q21-3)
            Assert.True(existing.Enabled);

            var plan = verify.ModelPricingPlans.Single(item => item.ModelInfoId == existingModelId);
            Assert.Equal(9m, verify.ModelPricingRules
                .Single(rule => rule.PricingPlanId == plan.Id).UnitPrice);
        }
    }

    [Fact]
    public void SyncOverwriteKeepsDisabledModelDisabled()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context, "test", "Test", ModelCatalogSources.Manual, 1);
            var model = AddModel(context, provider.Id, "existing", ModelMatchTypes.Exact, "existing", 1m);
            model.Enabled = false;
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = service.ImportModelCatalog(
            SyncPayloadWithNewAndExisting(), dryRun: false, OverwriteSyncOptions());

        Assert.True(result.Succeeded);

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var model = verify.ModelInfos.Single(item => item.ModelKey == "existing");
            Assert.False(model.Enabled); // remote Enabled=true but local was false -> stays false
        }
    }

    [Fact]
    public void SyncKeepLocalPricingWhenRemoteNull()
    {
        var dbPath = CreateDbPath();
        Guid modelId;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context, "test", "Test", ModelCatalogSources.Manual, 1);
            var model = AddModel(context, provider.Id, "existing", ModelMatchTypes.Exact, "existing", 1m);
            modelId = model.Id;
        }

        var service = CreateService(dbPath);
        var document = SyncPayloadWithNewAndExisting();
        document.Models[0].Pricing = null; // remote says: no pricing

        var result = service.ImportModelCatalog(document, dryRun: false, OverwriteSyncOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Payload!.PricingDeleted);

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            // Local pricing plan still exists
            Assert.Single(verify.ModelPricingPlans, item => item.ModelInfoId == modelId);
        }
    }

    [Fact]
    public void SyncDoesNotDeleteRemoteMissingModels()
    {
        var dbPath = CreateDbPath();
        Guid localOnlyModelId;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context, "test", "Test", ModelCatalogSources.Manual, 1);
            var model = AddModel(context, provider.Id, "local-only", ModelMatchTypes.Exact, "local-only", 5m);
            localOnlyModelId = model.Id;
        }

        var service = CreateService(dbPath);
        // Document has "existing" and "sync-new-model" but NOT "local-only"
        var result = service.ImportModelCatalog(
            SyncPayloadWithNewAndExisting(), dryRun: false, OverwriteSyncOptions());

        Assert.True(result.Succeeded);

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            // local-only model still exists (Q21-1 / Q4)
            Assert.Single(verify.ModelInfos, item => item.Id == localOnlyModelId);
        }
    }

    [Fact]
    public void ImportLocalJsonStillUsesOldSemantics()
    {
        var dbPath = CreateDbPath();
        Guid modelId;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context, "test", "Test", ModelCatalogSources.Manual, 1);
            var model = AddModel(context, provider.Id, "existing", ModelMatchTypes.Exact, "existing", 1m);
            modelId = model.Id;
        }

        var service = CreateService(dbPath);
        var document = SyncPayloadWithNewAndExisting();

        // Old 2-arg overload: SkipExistingModels=false, PreserveLocalEnabled=false,
        // KeepLocalPricingWhenRemoteNull=false, Source=manual
        var result = service.ImportModelCatalog(document, dryRun: false);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Payload!.Skipped);

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var model = verify.ModelInfos.Single(item => item.Id == modelId);
            // Old semantics: Enabled overwritten by remote (was true -> now false)
            Assert.False(model.Enabled);
            Assert.Equal(ModelCatalogSources.Manual, model.Source);
        }
    }

    [Fact]
    public void ImportMissingProviderReturns400Not500()
    {
        // Regression: when a model references a provider that exists in DB
        // but is not in the document's providers list, the old code threw
        // KeyNotFoundException (500). Now it should return 400.
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            // Provider exists in DB but won't be in the document
            var provider = AddProvider(context, "db-only", "DB Only", ModelCatalogSources.Manual, 1);
            // Model under that provider
            AddModel(context, provider.Id, "existing", ModelMatchTypes.Exact, "existing", 1m);
        }

        var service = CreateService(dbPath);
        // Document only has provider "test" but model references "db-only"
        var document = new ModelCatalogTransferDocument
        {
            Type = "model_catalog",
            Version = 1,
            ExportedAt = "2026-08-27T02:00:00Z",
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
                    ProviderCode = "db-only",
                    ModelKey = "existing",
                    MatchType = ModelMatchTypes.Exact,
                    MatchPattern = "existing",
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

        // Old 2-arg overload should also benefit from the fix
        var result = service.ImportModelCatalog(document, dryRun: false);
        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Code);
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

internal static class ModelCatalogServiceCostExtensions
{
    /// 与时段无关的测试统一用这个固定时刻:2026-01-05 周一 12:00 UTC。
    /// 未配置峰谷的价格计划在任何时刻都按基础单价计费,因此该值只是让签名完整。
    internal static readonly DateTimeOffset DefaultBillingInstant =
        new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    internal static Task<ModelPricingCalculationResult> CalculateCostAsync(
        this ModelCatalogService service,
        Guid? channelId,
        string? requestModel,
        string? upstreamModel,
        ModelUsageVector usage)
    {
        return service.CalculateCostAsync(
            channelId,
            requestModel,
            upstreamModel,
            usage,
            DefaultBillingInstant);
    }
}

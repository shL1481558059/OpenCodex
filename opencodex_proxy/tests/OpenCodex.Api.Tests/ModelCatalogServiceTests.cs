using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ModelCatalogServiceTests
{
    [Fact]
    public void SeedDefaultsIncludesZhipuGlm52WithCnyPricing()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var service = CreateService(dbPath);
        service.SeedDefaults();

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var provider = context.ModelProviders.Single(item => item.Code == "zhipu");
            Assert.Equal("智谱", provider.Name);

            var model = context.ModelInfos.Single(item => item.ModelKey == "glm-5.2");
            Assert.Equal(provider.Id, model.ProviderId);
            Assert.Equal("GLM-5.2", model.DisplayName);
            Assert.Equal(ModelMatchTypes.Exact, model.MatchType);
            Assert.Equal("glm-5.2", model.MatchPattern);

            var plan = context.ModelPricingPlans.Single(item => item.ModelInfoId == model.Id && item.ChannelId == null);
            Assert.Equal("CNY", plan.Currency);

            var rules = context.ModelPricingRules
                .Where(item => item.PricingPlanId == plan.Id)
                .ToDictionary(item => item.BillingItem);
            Assert.Equal(8m, rules[ModelBillingItems.Input].UnitPrice);
            Assert.Equal(28m, rules[ModelBillingItems.Output].UnitPrice);
            Assert.Equal(0m, rules[ModelBillingItems.CacheWrite].UnitPrice);
            Assert.Equal(2m, rules[ModelBillingItems.CacheRead].UnitPrice);
        }
    }

    [Fact]
    public void SeedDefaultsUpdatesSystemDefaultProviderName()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            AddProvider(context, "zhipu", "智普", ModelCatalogSources.SystemDefault, 999);
        }

        var service = CreateService(dbPath);
        service.SeedDefaults();

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var provider = context.ModelProviders.Single(item => item.Code == "zhipu");
            Assert.Equal("智谱", provider.Name);
            Assert.Equal(110, provider.SortOrder);
        }
    }

    [Fact]
    public void SeedDefaultsDoesNotOverwriteManualProviderName()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            AddProvider(context, "zhipu", "自定义智谱", ModelCatalogSources.Manual, 888);
        }

        var service = CreateService(dbPath);
        service.SeedDefaults();

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var provider = context.ModelProviders.Single(item => item.Code == "zhipu");
            Assert.Equal("自定义智谱", provider.Name);
            Assert.Equal(888, provider.SortOrder);
        }
    }

    [Fact]
    public void CalculateCostUsesMatchPriority()
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

        Assert.Equal(1m, service.CalculateCost(null, null, "model-x", null, Tokens(1_000_000)).Cost);
        Assert.Equal(2m, service.CalculateCost(null, null, "model-y", null, Tokens(1_000_000)).Cost);
        Assert.Equal(3m, service.CalculateCost(null, null, "other-x", null, Tokens(1_000_000)).Cost);
        Assert.Equal(4m, service.CalculateCost(null, null, "other-x-other", null, Tokens(1_000_000)).Cost);
    }

    [Fact]
    public void CalculateCostUsesUpstreamModelForGlobalPricing()
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
        var result = service.CalculateCost(null, "request-model", "upstream-model", null, Tokens(1_000_000));

        Assert.Equal(7m, result.Cost);
        Assert.Equal("global_model_match", result.Resolution);
        Assert.Equal("upstream-model", result.ModelKey);
    }

    [Fact]
    public void CalculateCostIgnoresResponseModelForPricing()
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
        var result = service.CalculateCost(null, "request-model", "upstream-model", "response-model", Tokens(1_000_000));

        Assert.Equal(2m, result.Cost);
        Assert.Equal("global_model_match", result.Resolution);
        Assert.Equal("upstream-model", result.ModelKey);
    }

    [Fact]
    public void CalculateCostUsesChannelMappingByUpstreamModel()
    {
        var dbPath = CreateDbPath();
        var channelId = Guid.NewGuid();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var provider = AddProvider(context);
            AddModel(context, provider.Id, "upstream-model", ModelMatchTypes.Exact, "upstream-model", 1m);
            var channelModel = AddModel(
                context,
                provider.Id,
                "channel-upstream-model",
                ModelMatchTypes.Exact,
                "upstream-model",
                9m,
                ModelInfoScopes.Channel,
                channelId);
            context.ChannelModelMappings.Add(new ChannelModelMapping
            {
                ChannelId = channelId,
                Position = 0,
                RequestModel = "request-alias",
                UpstreamModel = "upstream-model",
                SupportsImage = false,
                ModelInfoId = channelModel.Id,
                PricingMode = ChannelModelPricingModes.InheritGlobal,
                Enabled = true,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = service.CalculateCost(channelId, "request-alias", "upstream-model", "response-model", Tokens(1_000_000));

        Assert.Equal(9m, result.Cost);
        Assert.Equal("channel_mapping_model", result.Resolution);
        Assert.Equal("channel-upstream-model", result.ModelKey);
    }

    [Fact]
    public void CalculateCostDoesNotFallbackToRequestModel()
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
        var result = service.CalculateCost(null, "request-model", "missing-upstream-model", null, Tokens(1_000_000));

        Assert.Equal(0m, result.Cost);
        Assert.Equal("model_not_matched", result.Resolution);
    }

    [Fact]
    public void CalculateCostUsesChannelPricingOverride()
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
                SupportsImage = false,
                ModelInfoId = model.Id,
                PricingMode = ChannelModelPricingModes.OverridePricing,
                PricingPlanId = overridePlan.Id,
                Enabled = true,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var result = service.CalculateCost(channelId, "model-a", "model-a", null, Tokens(1_000_000));

        Assert.Equal(9m, result.Cost);
        Assert.Equal("channel_mapping_pricing_override", result.Resolution);
    }

    [Fact]
    public void CalculateCostSplitsCacheWriteAndCacheRead()
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
        var result = service.CalculateCost(
            null,
            null,
            "cache-model",
            null,
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

    private static ModelCatalogService CreateService(string dbPath)
    {
        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        return new ModelCatalogService(
            new EfRepository<ModelProvider>(context),
            new EfRepository<ModelInfo>(context),
            new EfRepository<ModelPricingPlan>(context),
            new EfRepository<ModelPricingRule>(context),
            new EfRepository<ChannelModelMapping>(context),
            new EfRepository<ModelPricing>(context));
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

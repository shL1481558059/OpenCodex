using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
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
            Assert.DoesNotContain(context.ModelProviders, item => item.Code == "unknown");

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
    public void SeedDefaultsDisablesRemovedSystemDefaultProviders()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            AddProvider(context, "unknown", "Unknown", ModelCatalogSources.SystemDefault, 1000);
        }

        var service = CreateService(dbPath);
        service.SeedDefaults();

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var provider = context.ModelProviders.Single(item => item.Code == "unknown");
            Assert.False(provider.Enabled);
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
    public void CalculateCostUsesChannelModelInfoByUpstreamModel()
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
        var result = service.CalculateCost(channelId, "request-alias", "upstream-model", "response-model", Tokens(1_000_000));

        Assert.Equal(9m, result.Cost);
        Assert.Equal("channel_model_override", result.Resolution);
        Assert.Equal("channel-upstream-model", result.ModelKey);
        Assert.Null(result.ModelInfoId);
        Assert.NotNull(result.ChannelModelInfoId);
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
    public void CalculateCostIgnoresLegacyChannelMappingPricingFields()
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

        Assert.Equal(1m, result.Cost);
        Assert.Equal("global_model_match", result.Resolution);
    }

    [Fact]
    public void ChannelModelInfoManagementOverridesAndRestoresGlobalPricing()
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

        var service = CreateService(dbPath);
        var listed = service.ListChannelModelInfos(channelId);

        Assert.True(listed.Succeeded);
        var item = Assert.Single(listed.Payload!.Models);
        Assert.False(item.Overridden);
        Assert.Equal("global-model", item.GlobalModel?.ModelKey);

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
        var overrideCost = service.CalculateCost(channelId, "request-model", "upstream-model", null, Tokens(1_000_000));
        Assert.Equal(7m, overrideCost.Cost);
        Assert.Equal("channel_model_override", overrideCost.Resolution);

        listed = service.ListChannelModelInfos(channelId);
        item = Assert.Single(listed.Payload!.Models);
        Assert.True(item.Overridden);
        Assert.Equal("channel-model", item.OverrideModel?.ModelKey);

        var restored = service.RestoreChannelModelInfo(channelId, saved.Payload!.Model.Id);

        Assert.True(restored.Succeeded);
        var globalCost = service.CalculateCost(channelId, "request-model", "upstream-model", null, Tokens(1_000_000));
        Assert.Equal(1m, globalCost.Cost);
        Assert.Equal("global_model_match", globalCost.Resolution);

        listed = service.ListChannelModelInfos(channelId);
        item = Assert.Single(listed.Payload!.Models);
        Assert.False(item.Overridden);
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
            SupportsImage = false,
            ModelInfoId = null,
            PricingMode = ChannelModelPricingModes.InheritGlobal,
            PricingPlanId = null,
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

    private static ModelCatalogService CreateService(string dbPath)
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
            new EfRepository<ModelPricing>(context),
            new TestWorkContext(TestUserId, "admin", "superadmin"));
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

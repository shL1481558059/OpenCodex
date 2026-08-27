using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Caching;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ModelCatalogSyncServiceTests
{
    [Fact]
    public async Task IncrementalSyncCreatesNewModelsFromRemote()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            AddProvider(context, "test", "Test");
            AddModel(context, "test", "existing", 1m);
        }

        var catalog = CreateCatalogService(dbPath);
        var document = CreateSyncDocument();
        var syncClient = new StubSyncClient(document);
        var service = new ModelCatalogSyncService(catalog, syncClient, CreateSettingsProvider());

        var dryRun = await service.SyncAsync("incremental", dryRun: true);
        Assert.True(dryRun.Succeeded);
        Assert.Equal(1, dryRun.Payload!.Models.Created);
        Assert.Equal(1, dryRun.Payload.Skipped);

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            Assert.DoesNotContain(verify.ModelInfos, item => item.ModelKey == "sync-new-model");
        }

        var imported = await service.SyncAsync("incremental", dryRun: false);
        Assert.True(imported.Succeeded);
        Assert.Equal(1, imported.Payload!.Models.Created);

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var newModel = verify.ModelInfos.Single(item => item.ModelKey == "sync-new-model");
            Assert.Equal(ModelCatalogSources.Sync, newModel.Source);
        }
    }

    [Fact]
    public async Task OverwriteSyncUpdatesExistingModels()
    {
        var dbPath = CreateDbPath();
        Guid existingModelId;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            AddProvider(context, "test", "Test");
            var model = AddModel(context, "test", "existing", 1m);
            existingModelId = model.Id;
        }

        var catalog = CreateCatalogService(dbPath);
        var document = CreateSyncDocument();
        var syncClient = new StubSyncClient(document);
        var service = new ModelCatalogSyncService(catalog, syncClient, CreateSettingsProvider());

        var result = await service.SyncAsync("overwrite", dryRun: false);
        Assert.True(result.Succeeded);
        Assert.Single(result.Payload!.OverwrittenModelKeys, "existing");

        using (var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var model = verify.ModelInfos.Single(item => item.Id == existingModelId);
            Assert.Equal("Existing Renamed", model.DisplayName);
            Assert.Equal(ModelCatalogSources.Sync, model.Source);
        }
    }

    [Fact]
    public async Task InvalidModeReturns400()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var catalog = CreateCatalogService(dbPath);
        var syncClient = new StubSyncClient(CreateSyncDocument());
        var service = new ModelCatalogSyncService(catalog, syncClient, CreateSettingsProvider());

        var result = await service.SyncAsync("invalid", dryRun: false);
        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Code);
    }

    [Fact]
    public async Task FetchFailureReturns400()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var catalog = CreateCatalogService(dbPath);
        var syncClient = new StubSyncClient(throwOnFetch: new InvalidOperationException("connection refused"));
        var service = new ModelCatalogSyncService(catalog, syncClient, CreateSettingsProvider());

        var result = await service.SyncAsync("incremental", dryRun: false);
        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Code);
        Assert.Contains("connection refused", result.Description!);
    }

    [Fact]
    public async Task FetchInvalidJsonReturns400()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var catalog = CreateCatalogService(dbPath);
        var syncClient = new StubSyncClient(throwOnFetch: new InvalidOperationException("sync JSON is invalid: bad json"));
        var service = new ModelCatalogSyncService(catalog, syncClient, CreateSettingsProvider());

        var result = await service.SyncAsync("incremental", dryRun: false);
        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Code);
    }

    [Fact]
    public async Task Version2Returns400()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
        }

        var catalog = CreateCatalogService(dbPath);
        var document = CreateSyncDocument();
        document.Version = 2;
        var syncClient = new StubSyncClient(document);
        var service = new ModelCatalogSyncService(catalog, syncClient, CreateSettingsProvider());

        var result = await service.SyncAsync("incremental", dryRun: false);
        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Code);
    }

    [Fact]
    public async Task DefaultModeIsIncremental()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            AddProvider(context, "test", "Test");
            AddModel(context, "test", "existing", 1m);
        }

        var catalog = CreateCatalogService(dbPath);
        var syncClient = new StubSyncClient(CreateSyncDocument());
        var service = new ModelCatalogSyncService(catalog, syncClient, CreateSettingsProvider());

        // mode defaults to "incremental" - existing model should be skipped
        var result = await service.SyncAsync("incremental", dryRun: true);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Payload!.Skipped);
        Assert.Equal(0, result.Payload.OverwrittenModelKeys.Count);
    }

    private static ModelCatalogTransferDocument CreateSyncDocument()
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

    private static ModelProvider AddProvider(IOpenCodexDbContext context, string code, string name)
    {
        var provider = new ModelProvider
        {
            Code = code,
            Name = name,
            Enabled = true,
            SortOrder = 1,
            Source = "test",
            CreatedAt = 1,
            UpdatedAt = 1
        };
        context.ModelProviders.Add(provider);
        context.SaveChanges();
        return provider;
    }

    private static ModelInfo AddModel(IOpenCodexDbContext context, string providerCode, string modelKey, decimal inputPrice)
    {
        var provider = context.ModelProviders.Single(p => p.Code == providerCode);
        var model = new ModelInfo
        {
            Scope = ModelInfoScopes.Global,
            ProviderId = provider.Id,
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
            Enabled = true,
            Source = "test",
            CreatedAt = 1,
            UpdatedAt = 1
        };
        context.ModelPricingPlans.Add(plan);
        context.SaveChanges();
        context.ModelPricingRules.Add(new ModelPricingRule
        {
            PricingPlanId = plan.Id,
            BillingItem = ModelBillingItems.Input,
            BillingMode = ModelBillingModes.PerMillionTokens,
            UnitPrice = inputPrice,
            TiersJson = "[]",
            Enabled = true
        });
        context.SaveChanges();
        return model;
    }

    private static ModelCatalogService CreateCatalogService(string dbPath)
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
            new TestWorkContext(Guid.Parse("99999999-9999-9999-9999-999999999901"), "admin", "superadmin"),
            new InMemoryCacheService());
    }

    private static IOpenCodexRuntimeSettingsProvider CreateSettingsProvider()
    {
        return new TestSettingsProvider();
    }

    private static string CreateDbPath()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-sync-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return dbPath;
    }

    private sealed class StubSyncClient : IModelCatalogSyncClient
    {
        private readonly ModelCatalogTransferDocument? _document;
        private readonly Exception? _throwOnFetch;

        public StubSyncClient(ModelCatalogTransferDocument? document = null, Exception? throwOnFetch = null)
        {
            _document = document;
            _throwOnFetch = throwOnFetch;
        }

        public Task<ModelCatalogTransferDocument> FetchAsync(string url)
        {
            if (_throwOnFetch is not null)
            {
                throw _throwOnFetch;
            }

            return Task.FromResult(_document!);
        }
    }

    private sealed class TestSettingsProvider : IOpenCodexRuntimeSettingsProvider
    {
        public OpenCodexRuntimeSettings GetSettings()
        {
            return new OpenCodexRuntimeSettings(
                "sqlite",
                "Data Source=:memory:",
                "admin",
                "password",
                120,
                modelCatalogSyncUrl: null); // null = use default
        }
    }

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
        public SessionUser RequireUser() => _user;
        public SessionUser RequireSuperadmin() =>
            IsSuperadmin ? _user : throw new UnauthorizedAccessException("superadmin required");
    }

    private sealed class InMemoryCacheService : ICacheService
    {
        public Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T?>> factory, TimeSpan? ttl = null)
            => factory();
        public Task RemoveAsync(string key) => Task.CompletedTask;
        public Task RemoveAsync(IEnumerable<string> keys) => Task.CompletedTask;
    }
}

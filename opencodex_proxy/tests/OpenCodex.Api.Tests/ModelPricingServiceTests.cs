using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ModelPricingServiceTests
{
    [Fact]
    public void CalculateCostUsesDatabasePricing()
    {
        var dbPath = CreateDbPath();

        using (var context = OpenCodexDbContextFactory.Create(dbPath))
        {
            context.Database.EnsureCreated();
            OpenCodexPricing.EnsureSchema(context);
            context.ModelPricings.Add(new ModelPricing
            {
                ModelId = "match-model",
                Vendor = "test",
                Name = "Match Model",
                MatchPattern = "match-model",
                InputPrice = 2,
                CachedInputPrice = null,
                OutputPrice = 4,
                Enabled = true,
                Source = "test",
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.SaveChanges();
        }

        var service = new ModelPricingService(new TestSettingsProvider(dbPath));
        var cost = service.CalculateCost("provider/match-model", 1_000, 200, 3_000);

        Assert.Equal(0.014, cost, precision: 6);
    }

    [Fact]
    public void ParseRemoteAnthropicPricingIncludesLatestOpusModels()
    {
        const string json = """
                            {
                              "vendor": "anthropic",
                              "models": [
                                {
                                  "id": "claude-opus-4-7",
                                  "name": "Claude Opus 4.7",
                                  "price_history": [
                                    {
                                      "input": 5,
                                      "output": 25,
                                      "from_date": null,
                                      "to_date": null,
                                      "input_cached": null
                                    }
                                  ]
                                },
                                {
                                  "id": "claude-opus-4-8",
                                  "name": "Claude Opus 4.8",
                                  "price_history": [
                                    {
                                      "input": 5,
                                      "output": 25,
                                      "from_date": null,
                                      "to_date": null,
                                      "input_cached": null
                                    }
                                  ]
                                }
                              ]
                            }
                            """;

        var prices = OpenCodexPricingDefaults.ParseRemoteDataFile(json);

        Assert.Contains(prices, price =>
            price.ModelId == "claude-opus-4-7"
            && price.Vendor == "anthropic"
            && price.InputPrice == 5
            && price.OutputPrice == 25);
        Assert.Contains(prices, price =>
            price.ModelId == "claude-opus-4-8"
            && price.Vendor == "anthropic"
            && price.InputPrice == 5
            && price.OutputPrice == 25);
    }

    [Fact]
    public void UpdateDefaultsUpdatesDefaultSourceAndSkipsManualSource()
    {
        var dbPath = CreateDbPath();
        using (var context = OpenCodexDbContextFactory.Create(dbPath))
        {
            context.Database.EnsureCreated();
            OpenCodexPricing.EnsureSchema(context);
            context.ModelPricings.AddRange(
                new ModelPricing
                {
                    ModelId = "default-model",
                    Vendor = "old",
                    Name = "Old",
                    MatchPattern = "default-model",
                    InputPrice = 1,
                    CachedInputPrice = null,
                    OutputPrice = 2,
                    Enabled = true,
                    Source = OpenCodexPricingDefaults.Source,
                    CreatedAt = 1,
                    UpdatedAt = 1
                },
                new ModelPricing
                {
                    ModelId = "manual-model",
                    Vendor = "manual",
                    Name = "Manual",
                    MatchPattern = "manual-model",
                    InputPrice = 9,
                    CachedInputPrice = null,
                    OutputPrice = 9,
                    Enabled = true,
                    Source = "manual",
                    CreatedAt = 1,
                    UpdatedAt = 1
                });
            context.SaveChanges();
        }

        var service = new ModelPricingService(new TestSettingsProvider(dbPath));
        var result = service.UpdateDefaults(
        [
            new DefaultModelPricing("default-model", "remote", "Remote", 3, 1, 4),
            new DefaultModelPricing("manual-model", "remote", "Remote Manual", 3, 1, 4),
            new DefaultModelPricing("new-model", "remote", "New", 5, null, 6)
        ]);

        Assert.Equal((1, 1, 1), result);
        using var verify = OpenCodexDbContextFactory.Create(dbPath);
        var defaultPrice = verify.ModelPricings.Single(price => price.ModelId == "default-model");
        var manualPrice = verify.ModelPricings.Single(price => price.ModelId == "manual-model");
        var newPrice = verify.ModelPricings.Single(price => price.ModelId == "new-model");
        Assert.Equal("remote", defaultPrice.Vendor);
        Assert.Equal(3, defaultPrice.InputPrice);
        Assert.Equal(1, defaultPrice.CachedInputPrice);
        Assert.Equal(4, defaultPrice.OutputPrice);
        Assert.Equal("manual", manualPrice.Source);
        Assert.Equal(9, manualPrice.InputPrice);
        Assert.Equal(OpenCodexPricingDefaults.Source, newPrice.Source);
    }

    [Fact]
    public void UpdatePriceMarksPricingAsManual()
    {
        var dbPath = CreateDbPath();
        long id;
        using (var context = OpenCodexDbContextFactory.Create(dbPath))
        {
            context.Database.EnsureCreated();
            OpenCodexPricing.EnsureSchema(context);
            var price = new ModelPricing
            {
                ModelId = "editable-model",
                Vendor = "remote",
                Name = "Editable",
                MatchPattern = "editable-model",
                InputPrice = 1,
                CachedInputPrice = null,
                OutputPrice = 2,
                Enabled = true,
                Source = OpenCodexPricingDefaults.Source,
                CreatedAt = 1,
                UpdatedAt = 1
            };
            context.ModelPricings.Add(price);
            context.SaveChanges();
            id = price.Id;
        }

        var service = new ModelPricingService(new TestSettingsProvider(dbPath));
        var result = service.UpdatePrice(
            id,
            new ModelPricingUpdateCommand(new Dictionary<string, object?>
            {
                ["input_price"] = 7.0
            }));

        Assert.True(result.Succeeded);
        using var verify = OpenCodexDbContextFactory.Create(dbPath);
        var updated = verify.ModelPricings.Single(price => price.Id == id);
        Assert.Equal("manual", updated.Source);
        Assert.Equal(7, updated.InputPrice);
    }

    private static string CreateDbPath()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return dbPath;
    }

    private sealed class TestSettingsProvider : IOpenCodexRuntimeSettingsProvider
    {
        private readonly OpenCodexRuntimeSettings _settings;

        public TestSettingsProvider(string dbPath)
        {
            _settings = new OpenCodexRuntimeSettings(
                dbPath,
                "admin",
                "password",
                120);
        }

        public OpenCodexRuntimeSettings GetSettings()
        {
            return _settings;
        }
    }
}

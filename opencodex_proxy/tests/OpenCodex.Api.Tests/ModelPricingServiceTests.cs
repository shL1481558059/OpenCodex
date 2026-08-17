using OpenCodex.Core.Domain;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Data;
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

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
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

        var service = CreateService(dbPath);
        var cost = service.CalculateCost("provider/match-model", 1_000, 200, 3_000);

        Assert.Equal(0.014, cost, precision: 6);
    }

    [Fact]
    public void UpdatePriceMarksPricingAsManual()
    {
        var dbPath = CreateDbPath();
        Guid id;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
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
                Source = "legacy-default",
                CreatedAt = 1,
                UpdatedAt = 1
            };
            context.ModelPricings.Add(price);
            context.SaveChanges();
            id = price.Id;
        }

        var service = CreateService(dbPath);
        var result = service.UpdatePrice(
            id,
            new ModelPricingUpdateCommand(new Dictionary<string, object?>
            {
                ["input_price"] = 7.0
            }));

        Assert.True(result.Succeeded);
        using var verify = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
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

    private static ModelPricingService CreateService(string dbPath)
    {
        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        return new ModelPricingService(new EfRepository<ModelPricing>(context));
    }
}

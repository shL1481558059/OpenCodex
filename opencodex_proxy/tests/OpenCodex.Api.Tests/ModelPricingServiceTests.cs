using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ModelPricingServiceTests
{
    [Fact]
    public void CalculateCostUsesDatabasePricing()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

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

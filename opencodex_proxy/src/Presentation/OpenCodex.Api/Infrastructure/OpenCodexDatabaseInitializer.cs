using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Infrastructure;

public static class OpenCodexDatabaseInitializer
{
    public static void Initialize(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var serviceProvider = scope.ServiceProvider;
        var context = serviceProvider.GetRequiredService<IOpenCodexDbContext>();
        context.Database.Migrate();
        SeedDefaultModelPricing(serviceProvider.GetRequiredService<IRepository<ModelPricing>>());
        serviceProvider.GetRequiredService<IModelCatalogService>().SeedDefaults();
    }

    private static void SeedDefaultModelPricing(IRepository<ModelPricing> pricingRepository)
    {
        if (pricingRepository.TableNoTracking.Any())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        foreach (var price in OpenCodexPricingDefaults.Current())
        {
            pricingRepository.Insert(new ModelPricing
            {
                ModelId = price.ModelId,
                Vendor = price.Vendor,
                Name = price.Name,
                MatchPattern = price.ModelId,
                InputPrice = price.InputPrice,
                CachedInputPrice = price.CachedInputPrice,
                OutputPrice = price.OutputPrice,
                Enabled = true,
                Source = OpenCodexPricingDefaults.Source,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
    }
}

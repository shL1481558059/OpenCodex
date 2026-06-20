using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.Core.Persistence;

namespace OpenCodex.Api.Infrastructure;

public static class OpenCodexDatabaseInitializer
{
    public static void Initialize(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var settings = scope.ServiceProvider
            .GetRequiredService<IOpenCodexRuntimeSettingsProvider>()
            .GetSettings();

        using var context = OpenCodexDbContextFactory.Create(settings.DatabaseProvider, settings.ConnectionString);
        context.Database.Migrate();
        SeedDefaultModelPricing(context);
    }

    private static void SeedDefaultModelPricing(OpenCodexDbContext context)
    {
        if (context.ModelPricings.Any())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        foreach (var price in OpenCodexPricingDefaults.Current())
        {
            context.ModelPricings.Add(new ModelPricing
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

        context.SaveChanges();
    }
}

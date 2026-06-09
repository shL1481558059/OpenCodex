using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Data;

namespace OpenCodex.Core.Persistence;

public static class OpenCodexPricing
{
    public static void EnsureSchema(OpenCodexDbContext context)
    {
        context.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "ModelPricings" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ModelPricings" PRIMARY KEY AUTOINCREMENT,
                "ModelId" TEXT NOT NULL,
                "Vendor" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "MatchPattern" TEXT NOT NULL,
                "InputPrice" REAL NOT NULL,
                "CachedInputPrice" REAL NULL,
                "OutputPrice" REAL NOT NULL,
                "Enabled" INTEGER NOT NULL DEFAULT 1,
                "Source" TEXT NOT NULL DEFAULT '',
                "CreatedAt" REAL NOT NULL DEFAULT 0,
                "UpdatedAt" REAL NOT NULL DEFAULT 0
            );
            """);
        context.Database.ExecuteSqlRaw(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ModelPricings_ModelId" ON "ModelPricings" ("ModelId");""");
        context.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_ModelPricings_Vendor" ON "ModelPricings" ("Vendor");""");
        context.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_ModelPricings_Enabled" ON "ModelPricings" ("Enabled");""");
        context.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_ModelPricings_MatchPattern" ON "ModelPricings" ("MatchPattern");""");
    }

    public static double CalculateCost(
        IReadOnlyList<ModelPricing> prices,
        string model,
        int inputTokens,
        int cachedTokens,
        int outputTokens)
    {
        var matched = Match(prices, model);
        if (matched is null)
        {
            return 0.0;
        }

        var nonCached = Math.Max(0, inputTokens - cachedTokens);
        var cachedPrice = matched.CachedInputPrice ?? matched.InputPrice;
        return (nonCached * matched.InputPrice + cachedTokens * cachedPrice + outputTokens * matched.OutputPrice)
            / 1_000_000.0;
    }

    private static ModelPricing? Match(
        IReadOnlyList<ModelPricing> prices,
        string model)
    {
        var modelLower = (model ?? string.Empty).Trim().ToLowerInvariant();
        if (modelLower.Length == 0)
        {
            return null;
        }

        ModelPricing? exact = null;
        var exactLength = 0;
        ModelPricing? contained = null;
        var containedLength = 0;
        foreach (var price in prices)
        {
            foreach (var key in MatchKeys(price))
            {
                var keyLower = key.ToLowerInvariant();
                if (modelLower == keyLower && keyLower.Length > exactLength)
                {
                    exact = price;
                    exactLength = keyLower.Length;
                    continue;
                }

                if (modelLower.Contains(keyLower, StringComparison.Ordinal)
                    && keyLower.Length > containedLength)
                {
                    contained = price;
                    containedLength = keyLower.Length;
                }
            }
        }

        return exact ?? contained;
    }

    private static IEnumerable<string> MatchKeys(ModelPricing price)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Add(keys, price.ModelId);
        Add(keys, price.MatchPattern);
        return keys;
    }

    private static void Add(HashSet<string> keys, string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > 0)
        {
            keys.Add(normalized);
        }
    }
}

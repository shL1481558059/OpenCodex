namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private sealed record PricingTier(
        int MinInputTokens,
        int? MaxInputTokens,
        double Input,
        double CachedInput,
        double Output);

    private sealed record PricingEntry(
        string Key,
        double? Input,
        double? CachedInput,
        double? Output,
        IReadOnlyList<PricingTier>? Tiers = null);

    private static readonly IReadOnlyList<PricingEntry> PricingEntries =
    [
        new("deepseek-v4-flash", 1, 0.02, 2),
        new("deepseek-v4-pro", 3, 0.025, 6),
        new(
            "glm-5.1",
            null,
            null,
            null,
            [
                new PricingTier(0, 32000, 6, 1.3, 24),
                new PricingTier(32000, null, 8, 2, 28)
            ]),
        new("gpt-5.4", 18.25, 1.82, 109.5),
        new("gpt-5.4-mini", 5.47, 0.55, 32.85),
        new("gpt-5.5", 36.5, 3.65, 219.0)
    ];

    public static double CalculateCost(
        string model,
        int inputTokens,
        int cachedTokens,
        int outputTokens)
    {
        var modelLower = (model ?? string.Empty).ToLowerInvariant();
        PricingEntry? matched = null;
        var bestLength = 0;
        foreach (var entry in PricingEntries)
        {
            if (modelLower.Contains(entry.Key, StringComparison.Ordinal)
                && entry.Key.Length > bestLength)
            {
                matched = entry;
                bestLength = entry.Key.Length;
            }
        }

        if (matched is null)
        {
            return 0.0;
        }

        double inputPrice;
        double cachedPrice;
        double outputPrice;
        if (matched.Tiers is not null)
        {
            var totalInput = inputTokens + cachedTokens;
            var tier = matched.Tiers.FirstOrDefault(item =>
                totalInput >= item.MinInputTokens
                && (item.MaxInputTokens is null || totalInput < item.MaxInputTokens));
            tier ??= matched.Tiers.LastOrDefault();
            if (tier is null)
            {
                return 0.0;
            }

            inputPrice = tier.Input;
            cachedPrice = tier.CachedInput;
            outputPrice = tier.Output;
        }
        else
        {
            inputPrice = matched.Input ?? 0.0;
            cachedPrice = matched.CachedInput ?? 0.0;
            outputPrice = matched.Output ?? 0.0;
        }

        var nonCached = Math.Max(0, inputTokens - cachedTokens);
        return (nonCached * inputPrice + cachedTokens * cachedPrice + outputTokens * outputPrice) / 1_000_000.0;
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCodex.Core.Persistence;

public static class OpenCodexPricingDefaults
{
    public const string Source = "simonw/llm-prices";

    private const string ResourceName = "OpenCodex.Core.Persistence.Seeds.llm-prices-current-v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<DefaultModelPricing> Current()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("default model pricing resource not found");
        var snapshot = JsonSerializer.Deserialize<LlmPricesSnapshot>(stream, JsonOptions)
            ?? throw new InvalidOperationException("default model pricing resource is invalid");

        return snapshot.Prices
            .Where(price => !string.IsNullOrWhiteSpace(price.Id))
            .Select(price => new DefaultModelPricing(
                price.Id.Trim(),
                (price.Vendor ?? string.Empty).Trim(),
                string.IsNullOrWhiteSpace(price.Name) ? price.Id.Trim() : price.Name.Trim(),
                price.Input,
                price.InputCached,
                price.Output))
            .DistinctBy(price => price.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class LlmPricesSnapshot
    {
        [JsonPropertyName("prices")]
        public List<LlmPrice> Prices { get; set; } = [];
    }

    private sealed class LlmPrice
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("vendor")]
        public string? Vendor { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("input")]
        public double Input { get; set; }

        [JsonPropertyName("input_cached")]
        public double? InputCached { get; set; }

        [JsonPropertyName("output")]
        public double Output { get; set; }
    }
}

public sealed class DefaultModelPricing
{
    public DefaultModelPricing(
        string modelId,
        string vendor,
        string name,
        double inputPrice,
        double? cachedInputPrice,
        double outputPrice)
    {
        ModelId = modelId;
        Vendor = vendor;
        Name = name;
        InputPrice = inputPrice;
        CachedInputPrice = cachedInputPrice;
        OutputPrice = outputPrice;
    }

    public string ModelId { get; }

    public string Vendor { get; }

    public string Name { get; }

    public double InputPrice { get; }

    public double? CachedInputPrice { get; }

    public double OutputPrice { get; }
}

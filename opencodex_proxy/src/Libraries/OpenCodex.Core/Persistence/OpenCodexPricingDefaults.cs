using System.Reflection;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCodex.Core.Persistence;

public static class OpenCodexPricingDefaults
{
    public const string Source = "simonw/llm-prices";

    private const string ResourceName = "OpenCodex.Core.Persistence.Seeds.llm-prices-current-v1.json";
    private const string RemoteDataDirectoryUrl = "https://api.github.com/repos/simonw/llm-prices/contents/data?ref=main";

    private static readonly HttpClient DefaultHttpClient = new();

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

    public static async Task<IReadOnlyList<DefaultModelPricing>> CurrentRemoteAsync(
        CancellationToken cancellationToken = default)
    {
        return await CurrentRemoteAsync(DefaultHttpClient, cancellationToken);
    }

    internal static async Task<IReadOnlyList<DefaultModelPricing>> CurrentRemoteAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        var filesJson = await GetStringAsync(httpClient, RemoteDataDirectoryUrl, cancellationToken);
        using var filesDocument = JsonDocument.Parse(filesJson);
        if (filesDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("remote pricing directory response is invalid");
        }

        var prices = new List<DefaultModelPricing>();
        foreach (var file in filesDocument.RootElement.EnumerateArray())
        {
            if (!IsPricingDataFile(file, out var downloadUrl))
            {
                continue;
            }

            var dataJson = await GetStringAsync(httpClient, downloadUrl, cancellationToken);
            prices.AddRange(ParseRemoteDataFile(dataJson));
        }

        if (prices.Count == 0)
        {
            throw new InvalidOperationException("remote pricing data is empty");
        }

        return prices
            .DistinctBy(price => price.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<DefaultModelPricing> ParseRemoteDataFile(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var vendor = ReadString(root, "vendor");
        if (!root.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var prices = new List<DefaultModelPricing>();
        foreach (var model in models.EnumerateArray())
        {
            var modelId = ReadString(model, "id").Trim();
            if (modelId.Length == 0
                || !model.TryGetProperty("price_history", out var history)
                || history.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var current = CurrentPrice(history);
            if (current is null
                || !TryReadDouble(current.Value, "input", out var input)
                || !TryReadDouble(current.Value, "output", out var output))
            {
                continue;
            }

            prices.Add(new DefaultModelPricing(
                modelId,
                vendor.Trim(),
                string.IsNullOrWhiteSpace(ReadString(model, "name")) ? modelId : ReadString(model, "name").Trim(),
                input,
                TryReadNullableDouble(current.Value, "input_cached"),
                output));
        }

        return prices;
    }

    private static async Task<string> GetStringAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OpenCodex", "1.0"));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static bool IsPricingDataFile(
        JsonElement file,
        out string downloadUrl)
    {
        downloadUrl = ReadString(file, "download_url");
        return string.Equals(ReadString(file, "type"), "file", StringComparison.Ordinal)
            && ReadString(file, "name").EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && downloadUrl.Length > 0;
    }

    private static JsonElement? CurrentPrice(JsonElement history)
    {
        JsonElement? fallback = null;
        foreach (var price in history.EnumerateArray())
        {
            fallback = price;
            if (!price.TryGetProperty("to_date", out var toDate)
                || toDate.ValueKind == JsonValueKind.Null
                || (toDate.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(toDate.GetString())))
            {
                return price;
            }
        }

        return fallback;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }

    private static double? TryReadNullableDouble(JsonElement element, string propertyName)
    {
        return TryReadDouble(element, propertyName, out var value) ? value : null;
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

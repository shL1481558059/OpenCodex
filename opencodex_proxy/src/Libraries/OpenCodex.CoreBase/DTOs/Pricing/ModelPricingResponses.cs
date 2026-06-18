using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.DTOs.Pricing;

/// <summary>
/// 表示模型定价列表响应。
/// </summary>
public sealed class ModelPricingListResponse
{
    public ModelPricingListResponse(IReadOnlyList<ModelPricingResponse> prices)
    {
        Prices = prices;
    }

    [JsonPropertyName("prices")]
    public IReadOnlyList<ModelPricingResponse> Prices { get; }

    public static ModelPricingListResponse From(IReadOnlyList<ModelPricingDto>? prices)
    {
        return new ModelPricingListResponse(prices?.Select(ModelPricingResponse.From).ToList() ?? []);
    }
}

/// <summary>
/// 表示单个模型定价响应载荷。
/// </summary>
public sealed class ModelPricingResponsePayload
{
    public ModelPricingResponsePayload(ModelPricingResponse price)
    {
        Price = price;
    }

    [JsonPropertyName("price")]
    public ModelPricingResponse Price { get; }

    public static ModelPricingResponsePayload From(ModelPricingDto price)
    {
        return new ModelPricingResponsePayload(ModelPricingResponse.From(price));
    }
}

/// <summary>
/// 表示删除模型定价的响应。
/// </summary>
public sealed class DeleteModelPricingResponse
{
    public DeleteModelPricingResponse(bool deleted, ModelPricingResponse price)
    {
        Deleted = deleted;
        Price = price;
    }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; }

    [JsonPropertyName("price")]
    public ModelPricingResponse Price { get; }

    public static DeleteModelPricingResponse From(ModelPricingDto price)
    {
        return new DeleteModelPricingResponse(true, ModelPricingResponse.From(price));
    }
}

/// <summary>
/// 表示默认模型定价补齐响应。
/// </summary>
public sealed class SeedModelPricingResponse
{
    public SeedModelPricingResponse(int inserted, int updated, int skipped)
    {
        Inserted = inserted;
        Updated = updated;
        Skipped = skipped;
    }

    [JsonPropertyName("inserted")]
    public int Inserted { get; }

    [JsonPropertyName("updated")]
    public int Updated { get; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; }
}

/// <summary>
/// 表示管理接口返回的模型定价。
/// </summary>
public sealed class ModelPricingResponse
{
    public ModelPricingResponse(
        long id,
        string modelId,
        string vendor,
        string name,
        string matchPattern,
        double inputPrice,
        double? cachedInputPrice,
        double outputPrice,
        bool enabled,
        string source,
        double createdAt,
        double updatedAt)
    {
        Id = id;
        ModelId = modelId;
        Vendor = vendor;
        Name = name;
        MatchPattern = matchPattern;
        InputPrice = inputPrice;
        CachedInputPrice = cachedInputPrice;
        OutputPrice = outputPrice;
        Enabled = enabled;
        Source = source;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    [JsonPropertyName("id")]
    public long Id { get; }

    [JsonPropertyName("model_id")]
    public string ModelId { get; }

    [JsonPropertyName("vendor")]
    public string Vendor { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("match_pattern")]
    public string MatchPattern { get; }

    [JsonPropertyName("input_price")]
    public double InputPrice { get; }

    [JsonPropertyName("cached_input_price")]
    public double? CachedInputPrice { get; }

    [JsonPropertyName("output_price")]
    public double OutputPrice { get; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    [JsonPropertyName("source")]
    public string Source { get; }

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; }

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; }

    public static ModelPricingResponse From(ModelPricingDto price)
    {
        return new ModelPricingResponse(
            price.Id,
            price.ModelId,
            price.Vendor,
            price.Name,
            price.MatchPattern,
            price.InputPrice,
            price.CachedInputPrice,
            price.OutputPrice,
            price.Enabled,
            price.Source,
            price.CreatedAt,
            price.UpdatedAt);
    }
}

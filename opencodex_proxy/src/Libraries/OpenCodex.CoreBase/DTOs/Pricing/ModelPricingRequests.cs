using System.Text.Json.Serialization;
using OpenCodex.CoreBase.Domain;

namespace OpenCodex.CoreBase.DTOs.Pricing;

/// <summary>
/// 表示创建模型定价的请求。
/// </summary>
public sealed class ModelPricingCreateRequest
{
    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("vendor")]
    public string Vendor { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("match_pattern")]
    public string MatchPattern { get; set; } = string.Empty;

    [JsonPropertyName("input_price")]
    public double InputPrice { get; set; }

    [JsonPropertyName("cached_input_price")]
    public double? CachedInputPrice { get; set; }

    [JsonPropertyName("output_price")]
    public double OutputPrice { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    public ModelPricingCreateCommand ToCommand()
    {
        return new ModelPricingCreateCommand(
            ModelId,
            Vendor,
            Name,
            MatchPattern,
            InputPrice,
            CachedInputPrice,
            OutputPrice,
            Enabled);
    }
}

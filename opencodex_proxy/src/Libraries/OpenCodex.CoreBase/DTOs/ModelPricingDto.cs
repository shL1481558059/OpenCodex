namespace OpenCodex.CoreBase.DTOs;

/// <summary>
/// 表示模型定价数据传输对象。
/// </summary>
public sealed class ModelPricingDto(
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
    public long Id { get; } = id;

    public string ModelId { get; } = modelId;

    public string Vendor { get; } = vendor;

    public string Name { get; } = name;

    public string MatchPattern { get; } = matchPattern;

    public double InputPrice { get; } = inputPrice;

    public double? CachedInputPrice { get; } = cachedInputPrice;

    public double OutputPrice { get; } = outputPrice;

    public bool Enabled { get; } = enabled;

    public string Source { get; } = source;

    public double CreatedAt { get; } = createdAt;

    public double UpdatedAt { get; } = updatedAt;
}

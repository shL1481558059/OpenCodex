namespace OpenCodex.CoreBase.Domain;

/// <summary>
/// 表示创建模型定价的命令。
/// </summary>
public sealed class ModelPricingCreateCommand
{
    public ModelPricingCreateCommand(
        string modelId,
        string vendor,
        string name,
        string matchPattern,
        double inputPrice,
        double? cachedInputPrice,
        double outputPrice,
        bool enabled)
    {
        ModelId = modelId;
        Vendor = vendor;
        Name = name;
        MatchPattern = matchPattern;
        InputPrice = inputPrice;
        CachedInputPrice = cachedInputPrice;
        OutputPrice = outputPrice;
        Enabled = enabled;
    }

    public string ModelId { get; }

    public string Vendor { get; }

    public string Name { get; }

    public string MatchPattern { get; }

    public double InputPrice { get; }

    public double? CachedInputPrice { get; }

    public double OutputPrice { get; }

    public bool Enabled { get; }
}

/// <summary>
/// 表示更新模型定价的命令。
/// </summary>
public sealed class ModelPricingUpdateCommand
{
    public ModelPricingUpdateCommand(IReadOnlyDictionary<string, object?> values)
    {
        Values = values;
    }

    public IReadOnlyDictionary<string, object?> Values { get; }
}

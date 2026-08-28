namespace OpenCodex.CoreBase.DTOs.Proxy;

/// <summary>
/// 表示代理请求路由后的通道和模型信息。
/// </summary>
/// <param name="channel">选中的通道配置。</param>
/// <param name="originalModel">原始请求模型名称。</param>
/// <param name="upstreamModel">映射后的上游模型名称。</param>
/// <param name="supportsImage">指示当前路由模型是否原生支持图片输入的值。</param>
/// <param name="matchedModelMapping">指示当前路由是否命中了显式模型映射。</param>
public sealed class ProxyRouteDto(
    Dictionary<string, object?> channel,
    string originalModel,
    string upstreamModel,
    bool supportsImage,
    bool matchedModelMapping)
{
    /// <summary>
    /// 获取选中的通道配置。
    /// </summary>
    public Dictionary<string, object?> Channel { get; } = channel;

    /// <summary>
    /// 获取原始请求模型名称。
    /// </summary>
    public string OriginalModel { get; } = originalModel;

    /// <summary>
    /// 获取映射后的上游模型名称。
    /// </summary>
    public string UpstreamModel { get; } = upstreamModel;

    /// <summary>
    /// 获取指示当前路由模型是否原生支持图片输入的值。
    /// </summary>
    public bool SupportsImage { get; } = supportsImage;

    /// <summary>
    /// 获取指示当前路由是否命中了显式模型映射的值。
    /// </summary>
    public bool MatchedModelMapping { get; } = matchedModelMapping;
}

/// <summary>
/// 表示代理对外暴露的模型及其输入能力。
/// </summary>
/// <param name="model">对外模型名称。</param>
/// <param name="supportsImage">指示模型是否支持图片输入的值。</param>
public sealed class ProxyModelCapabilityDto(
    string model,
    bool supportsImage)
{
    /// <summary>
    /// 获取对外模型名称。
    /// </summary>
    public string Model { get; } = model;

    /// <summary>
    /// 获取指示模型是否支持图片输入的值。
    /// </summary>
    public bool SupportsImage { get; } = supportsImage;
}

/// <summary>
/// 定义图片识别转移模型不可用的原因标识,用于错误文案与诊断。
/// </summary>
public static class VisionTransferUnavailableReasons
{
    /// <summary>该 owner 没有配置行。</summary>
    public const string NotConfigured = "not_configured";

    /// <summary>渠道已删除或已不属于该 owner。</summary>
    public const string ChannelDeletedOrDisabled = "channel_unavailable";

    /// <summary>渠道内不再存在被引用的模型映射。</summary>
    public const string ModelMappingMissing = "model_mapping_missing";

    /// <summary>模型的图片能力已被撤销。</summary>
    public const string ImageCapabilityRevoked = "image_capability_revoked";
}

/// <summary>
/// 表示某个 owner 的图片识别转移候选路由,按主、兜底顺序排列。
/// </summary>
/// <param name="configured">指示该 owner 是否存在配置行的值。</param>
/// <param name="candidates">按优先顺序排列的候选路由,长度 0 到 2。</param>
/// <param name="unavailableReason">候选为空时的原因标识;候选非空时为空字符串。</param>
public sealed class VisionTransferRoutesDto(
    bool configured,
    IReadOnlyList<ProxyRouteDto> candidates,
    string unavailableReason)
{
    /// <summary>
    /// 获取指示该 owner 是否存在配置行的值。
    /// </summary>
    public bool Configured { get; } = configured;

    /// <summary>
    /// 获取按优先顺序排列的候选路由。
    /// </summary>
    public IReadOnlyList<ProxyRouteDto> Candidates { get; } = candidates;

    /// <summary>
    /// 获取候选为空时的原因标识。
    /// </summary>
    public string UnavailableReason { get; } = unavailableReason;

    /// <summary>
    /// 创建一个未配置的空结果。
    /// </summary>
    public static VisionTransferRoutesDto NotConfigured()
    {
        return new VisionTransferRoutesDto(false, [], VisionTransferUnavailableReasons.NotConfigured);
    }
}

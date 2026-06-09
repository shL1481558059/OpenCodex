namespace OpenCodex.CoreBase.DTOs.Proxy;

/// <summary>
/// 表示代理请求路由后的通道和模型信息。
/// </summary>
/// <param name="channel">选中的通道配置。</param>
/// <param name="originalModel">原始请求模型名称。</param>
/// <param name="upstreamModel">映射后的上游模型名称。</param>
public sealed class ProxyRouteDto(
    Dictionary<string, object?> channel,
    string originalModel,
    string upstreamModel)
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

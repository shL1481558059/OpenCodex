using OpenCodex.CoreBase.DTOs.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义代理通道路由服务。
/// </summary>
public interface IProxyRouteService
{
    /// <summary>
    /// 为指定用户和模型选择代理通道。
    /// </summary>
    /// <param name="ownerUsername">访问密钥所属用户名。</param>
    /// <param name="model">请求模型名称。</param>
    /// <param name="requestContainsImages">指示请求是否包含图片输入。</param>
    /// <returns>代理路由结果。</returns>
    Task<ProxyRouteDto> ChooseRouteAsync(string ownerUsername, string? model, bool requestContainsImages = false);

    /// <summary>
    /// 为指定用户和模型列出按优先顺序排列的代理通道候选。
    /// </summary>
    /// <param name="ownerUsername">访问密钥所属用户名。</param>
    /// <param name="model">请求模型名称。</param>
    /// <param name="requestContainsImages">指示请求是否包含图片输入。</param>
    /// <returns>按优先级排序后的代理路由候选。</returns>
    Task<IReadOnlyList<ProxyRouteDto>> ListRouteCandidatesAsync(
        string ownerUsername,
        string? model,
        bool requestContainsImages = false);

    async Task<ProxyRouteDto> ChooseRouteAsync(
        string ownerUsername,
        string? model,
        bool requestContainsImages,
        IReadOnlySet<string>? allowedChannelTypes)
    {
        var candidates = await ListRouteCandidatesAsync(
            ownerUsername,
            model,
            requestContainsImages,
            allowedChannelTypes);
        return candidates.Count > 0
            ? candidates[0]
            : throw new InvalidOperationException("no route candidate matches the allowed channel types");
    }

    async Task<IReadOnlyList<ProxyRouteDto>> ListRouteCandidatesAsync(
        string ownerUsername,
        string? model,
        bool requestContainsImages,
        IReadOnlySet<string>? allowedChannelTypes)
    {
        var candidates = await ListRouteCandidatesAsync(ownerUsername, model, requestContainsImages);
        if (allowedChannelTypes is null)
        {
            return candidates;
        }

        return candidates
            .Where(candidate => allowedChannelTypes.Contains(
                candidate.Channel.TryGetValue("type", out var value) ? value?.ToString() ?? string.Empty : string.Empty))
            .ToList();
    }

    /// <summary>
    /// 为指定用户和模型选择 OCR 视觉来源通道。
    /// </summary>
    /// <param name="ownerUsername">访问密钥所属用户名。</param>
    /// <param name="model">请求模型名称。</param>
    /// <returns>OCR 视觉来源路由；未找到时返回 null。</returns>
    Task<ProxyRouteDto?> ChooseOcrRouteAsync(string ownerUsername, string? model);

    /// <summary>
    /// 列出指定用户可通过代理访问的对外模型名称。
    /// </summary>
    /// <param name="ownerUsername">访问密钥所属用户名。</param>
    /// <returns>可访问的模型名称列表。</returns>
    Task<IReadOnlyList<string>> ListModelsAsync(string ownerUsername);

    /// <summary>
    /// 列出指定用户可通过代理访问的对外模型及其输入能力。
    /// </summary>
    /// <param name="ownerUsername">访问密钥所属用户名。</param>
    /// <returns>可访问的模型能力列表。</returns>
    Task<IReadOnlyList<ProxyModelCapabilityDto>> ListModelCapabilitiesAsync(string ownerUsername);
}

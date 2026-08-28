using OpenCodex.CoreBase.DTOs.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义代理通道路由服务。
/// </summary>
public interface IProxyRouteService
{
    /// <summary>
    /// 为指定用户和模型列出按优先顺序排列的代理通道候选。
    /// </summary>
    /// <param name="ownerUsername">访问密钥所属用户名。</param>
    /// <param name="model">请求模型名称。</param>
    /// <returns>按优先级排序后的代理路由候选。</returns>
    Task<IReadOnlyList<ProxyRouteDto>> ListRouteCandidatesAsync(string ownerUsername, string? model);

    /// <summary>
    /// 在指定通道类型范围内列出代理通道候选。实现方可在展开渠道阶段就完成过滤,
    /// 默认实现只做候选后置过滤。
    /// </summary>
    /// <param name="ownerUsername">访问密钥所属用户名。</param>
    /// <param name="model">请求模型名称。</param>
    /// <param name="allowedChannelTypes">允许的通道类型;为 null 表示不限制。</param>
    /// <returns>按优先级排序后的代理路由候选。</returns>
    async Task<IReadOnlyList<ProxyRouteDto>> ListRouteCandidatesAsync(
        string ownerUsername,
        string? model,
        IReadOnlySet<string>? allowedChannelTypes)
    {
        var candidates = await ListRouteCandidatesAsync(ownerUsername, model);
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
    /// 列出指定用户配置的图片识别转移候选路由,按主、兜底顺序排列。
    /// 该结果只来自显式配置,不做任何自动发现。
    /// </summary>
    /// <param name="ownerUsername">访问密钥所属用户名。</param>
    /// <returns>候选路由与不可用原因。</returns>
    Task<VisionTransferRoutesDto> ListVisionTransferRoutesAsync(string ownerUsername);

    /// <summary>
    /// 列出指定用户可通过代理访问的对外模型及其输入能力。
    /// </summary>
    /// <param name="ownerUsername">访问密钥所属用户名。</param>
    /// <returns>可访问的模型能力列表。</returns>
    Task<IReadOnlyList<ProxyModelCapabilityDto>> ListModelCapabilitiesAsync(string ownerUsername);
}

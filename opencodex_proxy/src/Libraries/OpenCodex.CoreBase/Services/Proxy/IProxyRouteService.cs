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
    /// <returns>代理路由结果。</returns>
    ProxyRouteDto ChooseRoute(string ownerUsername, string? model);

    /// <summary>
    /// 列出指定用户可通过代理访问的对外模型名称。
    /// </summary>
    /// <param name="ownerUsername">访问密钥所属用户名。</param>
    /// <returns>可访问的模型名称列表。</returns>
    IReadOnlyList<string> ListModels(string ownerUsername);
}

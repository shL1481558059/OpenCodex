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
}

using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义代理请求已认证身份的读取入口。
/// </summary>
public interface IProxyIdentityContext
{
    /// <summary>
    /// 获取当前请求是否存在已认证的代理身份。
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// 获取当前请求已认证的代理身份。
    /// </summary>
    ProxyIdentity? Current { get; }

    /// <summary>
    /// 要求当前请求存在已认证的代理身份。
    /// </summary>
    /// <returns>已认证的代理身份。</returns>
    ProxyIdentity RequireIdentity();
}

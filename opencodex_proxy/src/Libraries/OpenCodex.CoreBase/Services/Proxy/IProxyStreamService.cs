using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义流式代理请求服务。
/// </summary>
public interface IProxyStreamService
{
    /// <summary>
    /// 向上游发送流式代理请求。
    /// </summary>
    /// <param name="context">流式代理上下文。</param>
    /// <returns>表示异步处理过程的任务。</returns>
    Task StreamAsync(ProxyStreamContext context);
}

using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义代理请求日志写入服务。
/// </summary>
public interface IProxyLogService
{
    long CreateQueuedLog(ProxyRequestLogQueuedContext context);

    void MarkProcessing(long requestLogId, ProxyRequestLogProcessingContext context);

    void CompleteLog(long requestLogId, ProxyLogContext context, ProxyRequestMetadata request);

    /// <summary>
    /// 根据代理日志上下文和请求元数据写入日志。
    /// </summary>
    /// <param name="context">代理日志上下文。</param>
    /// <param name="request">请求元数据。</param>
    /// <returns>写入后的日志标识。</returns>
    long WriteLog(ProxyLogContext context, ProxyRequestMetadata request);

    /// <summary>
    /// 根据完整请求日志上下文写入日志。
    /// </summary>
    /// <param name="context">完整请求日志上下文。</param>
    /// <returns>写入后的日志标识。</returns>
    long WriteLog(ProxyRequestLogContext context);
}

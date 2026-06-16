using OpenCodex.CoreBase.DTOs.ChannelDiagnostics;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义后台通道诊断服务。
/// </summary>
public interface IChannelDiagnosticsService
{
    /// <summary>
    /// 发现指定通道可用的模型列表。
    /// </summary>
    /// <param name="body">诊断请求内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发现模型结果。</returns>
    Task<ApiOpResult<DiscoverModelsResponse>> DiscoverModelsAsync(
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken);

    /// <summary>
    /// 测试指定通道的请求连通性。
    /// </summary>
    /// <param name="body">诊断请求内容。</param>
    /// <param name="user">发起诊断的后台用户。</param>
    /// <param name="requestMetadata">传入请求元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>通道测试结果。</returns>
    Task<ApiOpResult<TestChannelResponse>> TestChannelAsync(
        IReadOnlyDictionary<string, object?> body,
        SessionUser user,
        ProxyRequestMetadata requestMetadata,
        CancellationToken cancellationToken);

    /// <summary>
    /// 以流式方式测试指定通道的请求连通性。
    /// </summary>
    /// <param name="body">诊断请求内容。</param>
    /// <param name="user">发起诊断的后台用户。</param>
    /// <param name="requestMetadata">传入请求元数据。</param>
    /// <param name="writer">流式响应写入器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步流式测试已完成的任务。</returns>
    Task StreamTestChannelAsync(
        IReadOnlyDictionary<string, object?> body,
        SessionUser user,
        ProxyRequestMetadata requestMetadata,
        IProxyStreamWriter writer,
        CancellationToken cancellationToken);
}

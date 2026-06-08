using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// 包含处理传入代理端点请求所需的全部输入。
/// </summary>
public sealed class ProxyEndpointContext
{
    /// <summary>
    /// 初始化 <see cref="ProxyEndpointContext"/> 类的新实例。
    /// </summary>
    /// <param name="entryProtocol">传入请求使用的协议形态。</param>
    /// <param name="payload">标准化后的传入请求载荷。</param>
    /// <param name="authorizationHeader">传入的授权头（如果提供）。</param>
    /// <param name="requestMetadata">传入请求元数据。</param>
    /// <param name="streamWriter">流式响应使用的写入器。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    public ProxyEndpointContext(
        string entryProtocol,
        Dictionary<string, object?>? payload,
        string? authorizationHeader,
        ProxyRequestMetadata requestMetadata,
        IProxyStreamWriter streamWriter,
        CancellationToken cancellationToken)
    {
        EntryProtocol = entryProtocol;
        Payload = payload;
        AuthorizationHeader = authorizationHeader;
        RequestMetadata = requestMetadata;
        StreamWriter = streamWriter;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// 获取传入请求使用的协议形态。
    /// </summary>
    public string EntryProtocol { get; }

    /// <summary>
    /// 获取标准化后的传入请求载荷。
    /// </summary>
    public Dictionary<string, object?>? Payload { get; }

    /// <summary>
    /// 获取传入的授权头（如果提供）。
    /// </summary>
    public string? AuthorizationHeader { get; }

    /// <summary>
    /// 获取传入请求元数据。
    /// </summary>
    public ProxyRequestMetadata RequestMetadata { get; }

    /// <summary>
    /// 获取流式响应使用的写入器。
    /// </summary>
    public IProxyStreamWriter StreamWriter { get; }

    /// <summary>
    /// 获取请求取消令牌。
    /// </summary>
    public CancellationToken CancellationToken { get; }
}

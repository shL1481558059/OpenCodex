using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs.Proxy;

namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// 包含执行非流式代理请求所需的值。
/// </summary>
public sealed class ProxyNonStreamContext
{
    /// <summary>
    /// 初始化 <see cref="ProxyNonStreamContext"/> 类的新实例。
    /// </summary>
    /// <param name="startedTimestamp">请求开始时间戳，单位为毫秒。</param>
    /// <param name="requestId">唯一请求标识符。</param>
    /// <param name="ownerUsername">拥有该请求的用户名。</param>
    /// <param name="apiKeyId">请求使用的访问密钥标识符（如果可用）。</param>
    /// <param name="payload">标准化后的传入请求载荷。</param>
    /// <param name="upstreamRequest">要发送到上游的请求载荷。</param>
    /// <param name="entryProtocol">传入请求使用的协议形态。</param>
    /// <param name="route">已解析的代理路由。</param>
    /// <param name="channelType">选中的通道类型。</param>
    /// <param name="channelId">选中的通道标识符。</param>
    /// <param name="ownerRole">与请求所有者关联的角色。</param>
    /// <param name="upstreamModel">路由到上游提供方的模型。</param>
    /// <param name="requestModel">调用方请求的模型（如果可用）。</param>
    /// <param name="defaultTimeout">默认上游超时时间，单位为秒。</param>
    /// <param name="requestMetadata">传入请求元数据。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    public ProxyNonStreamContext(
        long startedTimestamp,
        string requestId,
        string ownerUsername,
        long? apiKeyId,
        Dictionary<string, object?> payload,
        Dictionary<string, object?> upstreamRequest,
        string entryProtocol,
        ProxyRouteDto route,
        string channelType,
        string channelId,
        string ownerRole,
        string upstreamModel,
        string? requestModel,
        int defaultTimeout,
        ProxyRequestMetadata requestMetadata,
        CancellationToken cancellationToken)
    {
        StartedTimestamp = startedTimestamp;
        RequestId = requestId;
        OwnerUsername = ownerUsername;
        ApiKeyId = apiKeyId;
        Payload = payload;
        UpstreamRequest = upstreamRequest;
        EntryProtocol = entryProtocol;
        Route = route;
        ChannelType = channelType;
        ChannelId = channelId;
        OwnerRole = ownerRole;
        UpstreamModel = upstreamModel;
        RequestModel = requestModel;
        DefaultTimeout = defaultTimeout;
        RequestMetadata = requestMetadata;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// 获取请求开始时间戳，单位为毫秒。
    /// </summary>
    public long StartedTimestamp { get; }

    /// <summary>
    /// 获取唯一请求标识符。
    /// </summary>
    public string RequestId { get; }

    /// <summary>
    /// 获取拥有该请求的用户名。
    /// </summary>
    public string OwnerUsername { get; }

    /// <summary>
    /// 获取请求使用的访问密钥标识符（如果可用）。
    /// </summary>
    public long? ApiKeyId { get; }

    /// <summary>
    /// 获取标准化后的传入请求载荷。
    /// </summary>
    public Dictionary<string, object?> Payload { get; }

    /// <summary>
    /// 获取要发送到上游的请求载荷。
    /// </summary>
    public Dictionary<string, object?> UpstreamRequest { get; }

    /// <summary>
    /// 获取传入请求使用的协议形态。
    /// </summary>
    public string EntryProtocol { get; }

    /// <summary>
    /// 获取已解析的代理路由。
    /// </summary>
    public ProxyRouteDto Route { get; }

    /// <summary>
    /// 获取选中的通道类型。
    /// </summary>
    public string ChannelType { get; }

    /// <summary>
    /// 获取选中的通道标识符。
    /// </summary>
    public string ChannelId { get; }

    /// <summary>
    /// 获取与请求所有者关联的角色。
    /// </summary>
    public string OwnerRole { get; }

    /// <summary>
    /// 获取路由到上游提供方的模型。
    /// </summary>
    public string UpstreamModel { get; }

    /// <summary>
    /// 获取调用方请求的模型（如果可用）。
    /// </summary>
    public string? RequestModel { get; }

    /// <summary>
    /// 获取默认上游超时时间，单位为秒。
    /// </summary>
    public int DefaultTimeout { get; }

    /// <summary>
    /// 获取传入请求元数据。
    /// </summary>
    public ProxyRequestMetadata RequestMetadata { get; }

    /// <summary>
    /// 获取请求取消令牌。
    /// </summary>
    public CancellationToken CancellationToken { get; }
}

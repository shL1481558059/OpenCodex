using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Routing;

namespace OpenCodex.CoreBase.Services.Proxy;

public sealed class ProxyStreamContext
{
    public ProxyStreamContext(
        long startedTimestamp,
        string requestId,
        string ownerUsername,
        long? apiKeyId,
        Dictionary<string, object?> payload,
        Dictionary<string, object?> upstreamRequest,
        string entryProtocol,
        RouteResult route,
        string channelType,
        string channelId,
        string ownerRole,
        string upstreamModel,
        string? requestModel,
        int defaultTimeout,
        ProxyRequestMetadata requestMetadata,
        IProxyStreamWriter streamWriter,
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
        StreamWriter = streamWriter;
        CancellationToken = cancellationToken;
    }

    public long StartedTimestamp { get; }

    public string RequestId { get; }

    public string OwnerUsername { get; }

    public long? ApiKeyId { get; }

    public Dictionary<string, object?> Payload { get; }

    public Dictionary<string, object?> UpstreamRequest { get; }

    public string EntryProtocol { get; }

    public RouteResult Route { get; }

    public string ChannelType { get; }

    public string ChannelId { get; }

    public string OwnerRole { get; }

    public string UpstreamModel { get; }

    public string? RequestModel { get; }

    public int DefaultTimeout { get; }

    public ProxyRequestMetadata RequestMetadata { get; }

    public IProxyStreamWriter StreamWriter { get; }

    public CancellationToken CancellationToken { get; }
}

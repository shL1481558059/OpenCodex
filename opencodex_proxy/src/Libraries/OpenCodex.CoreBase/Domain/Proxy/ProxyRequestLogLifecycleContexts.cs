namespace OpenCodex.CoreBase.Domain.Proxy;

public sealed class ProxyRequestLogQueuedContext
{
    public ProxyRequestLogQueuedContext(
        string requestId,
        string ownerUsername,
        long? apiKeyId,
        Dictionary<string, object?>? payload,
        string? requestModel,
        bool isStream,
        string method,
        string path,
        string? clientIp,
        IReadOnlyDictionary<string, string> requestHeaders,
        string requestType = ProxyRequestTypes.Main,
        long? parentRequestLogId = null)
    {
        RequestId = requestId;
        OwnerUsername = ownerUsername;
        ApiKeyId = apiKeyId;
        Payload = payload;
        RequestModel = requestModel;
        IsStream = isStream;
        Method = method;
        Path = path;
        ClientIp = clientIp;
        RequestHeaders = requestHeaders;
        RequestType = requestType;
        ParentRequestLogId = parentRequestLogId;
    }

    public string RequestId { get; }

    public string OwnerUsername { get; }

    public long? ApiKeyId { get; }

    public Dictionary<string, object?>? Payload { get; }

    public string? RequestModel { get; }

    public bool IsStream { get; }

    public string Method { get; }

    public string Path { get; }

    public string? ClientIp { get; }

    public IReadOnlyDictionary<string, string> RequestHeaders { get; }

    public string RequestType { get; }

    public long? ParentRequestLogId { get; }
}

public sealed class ProxyRequestLogProcessingContext
{
    public ProxyRequestLogProcessingContext(
        string ownerUsername,
        long? apiKeyId,
        Dictionary<string, object?>? upstreamRequest,
        string? requestModel,
        string? upstreamModel,
        string? channelId,
        string? channelType,
        bool isStream)
    {
        OwnerUsername = ownerUsername;
        ApiKeyId = apiKeyId;
        UpstreamRequest = upstreamRequest;
        RequestModel = requestModel;
        UpstreamModel = upstreamModel;
        ChannelId = channelId;
        ChannelType = channelType;
        IsStream = isStream;
    }

    public string OwnerUsername { get; }

    public long? ApiKeyId { get; }

    public Dictionary<string, object?>? UpstreamRequest { get; }

    public string? RequestModel { get; }

    public string? UpstreamModel { get; }

    public string? ChannelId { get; }

    public string? ChannelType { get; }

    public bool IsStream { get; }
}

public sealed class ProxyRequestStreamLineCapture
{
    public ProxyRequestStreamLineCapture(int sequence, double occurredAt, string source, string rawLine)
    {
        Sequence = sequence;
        OccurredAt = occurredAt;
        Source = source;
        RawLine = rawLine;
    }

    public int Sequence { get; }

    public double OccurredAt { get; }

    public string Source { get; }

    public string RawLine { get; }
}

namespace OpenCodex.CoreBase.Domain.Proxy;

public sealed class ProxyRequestLogQueuedContext
{
    public ProxyRequestLogQueuedContext(
        string requestId,
        string ownerUsername,
        Guid? apiKeyId,
        Dictionary<string, object?>? payload,
        string? requestModel,
        bool isStream,
        string method,
        string path,
        string? clientIp,
        IReadOnlyDictionary<string, string> requestHeaders,
        string requestType = ProxyRequestTypes.Main,
        Guid? parentRequestLogId = null,
        ReadOnlyMemory<byte>? rawRequestBody = null)
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
        RawRequestBody = rawRequestBody;
    }

    public string RequestId { get; }

    public string OwnerUsername { get; }

    public Guid? ApiKeyId { get; }

    public Dictionary<string, object?>? Payload { get; }

    public string? RequestModel { get; }

    public bool IsStream { get; }

    public string Method { get; }

    public string Path { get; }

    public string? ClientIp { get; }

    public IReadOnlyDictionary<string, string> RequestHeaders { get; }

    public string RequestType { get; }

    public Guid? ParentRequestLogId { get; }

    /// <summary>
    /// 获取入口读取到的原始 UTF-8 请求正文字节（如果可用）。
    /// </summary>
    public ReadOnlyMemory<byte>? RawRequestBody { get; }
}

public sealed class ProxyRequestLogProcessingContext
{
    public ProxyRequestLogProcessingContext(
        string ownerUsername,
        Guid? apiKeyId,
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

    public Guid? ApiKeyId { get; }

    public Dictionary<string, object?>? UpstreamRequest { get; }

    public string? RequestModel { get; }

    public string? UpstreamModel { get; }

    public string? ChannelId { get; }

    public string? ChannelType { get; }

    public bool IsStream { get; }
}

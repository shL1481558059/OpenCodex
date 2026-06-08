namespace OpenCodex.CoreBase.Services.Proxy;

public sealed class ProxyRequestLogContext
{
    public ProxyRequestLogContext(
        string requestId,
        string ownerUsername,
        long? apiKeyId,
        Dictionary<string, object?>? payload,
        Dictionary<string, object?>? upstreamRequest,
        Dictionary<string, object?>? upstreamResponse,
        Dictionary<string, object?>? responsePayload,
        object? errorResponse,
        string? requestModel,
        string? upstreamModel,
        string? channelId,
        string? channelType,
        bool isStream,
        int? ttftMs,
        int statusCode,
        int durationMs,
        string? error,
        Dictionary<string, object?>? webSearchDetails,
        string method,
        string path,
        string? clientIp,
        IReadOnlyDictionary<string, string> requestHeaders)
    {
        RequestId = requestId;
        OwnerUsername = ownerUsername;
        ApiKeyId = apiKeyId;
        Payload = payload;
        UpstreamRequest = upstreamRequest;
        UpstreamResponse = upstreamResponse;
        ResponsePayload = responsePayload;
        ErrorResponse = errorResponse;
        RequestModel = requestModel;
        UpstreamModel = upstreamModel;
        ChannelId = channelId;
        ChannelType = channelType;
        IsStream = isStream;
        TtftMs = ttftMs;
        StatusCode = statusCode;
        DurationMs = durationMs;
        Error = error;
        WebSearchDetails = webSearchDetails;
        Method = method;
        Path = path;
        ClientIp = clientIp;
        RequestHeaders = requestHeaders;
    }

    public string RequestId { get; }

    public string OwnerUsername { get; }

    public long? ApiKeyId { get; }

    public Dictionary<string, object?>? Payload { get; }

    public Dictionary<string, object?>? UpstreamRequest { get; }

    public Dictionary<string, object?>? UpstreamResponse { get; }

    public Dictionary<string, object?>? ResponsePayload { get; }

    public object? ErrorResponse { get; }

    public string? RequestModel { get; }

    public string? UpstreamModel { get; }

    public string? ChannelId { get; }

    public string? ChannelType { get; }

    public bool IsStream { get; }

    public int? TtftMs { get; }

    public int StatusCode { get; }

    public int DurationMs { get; }

    public string? Error { get; }

    public Dictionary<string, object?>? WebSearchDetails { get; }

    public string Method { get; }

    public string Path { get; }

    public string? ClientIp { get; }

    public IReadOnlyDictionary<string, string> RequestHeaders { get; }
}

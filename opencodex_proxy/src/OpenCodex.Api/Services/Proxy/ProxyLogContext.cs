namespace OpenCodex.Api.Services;

public sealed class ProxyLogContext
{
    public ProxyLogContext(
        string RequestId,
        string OwnerUsername,
        long? ApiKeyId,
        Dictionary<string, object?>? Payload,
        Dictionary<string, object?>? UpstreamRequest,
        Dictionary<string, object?>? UpstreamResponse,
        Dictionary<string, object?>? ResponsePayload,
        object? ErrorResponse,
        string? RequestModel,
        string? UpstreamModel,
        string? ChannelId,
        string? ChannelType,
        bool IsStream,
        int? TtftMs,
        int StatusCode,
        int DurationMs,
        string? Error,
        Dictionary<string, object?>? WebSearchDetails)
    {
        this.RequestId = RequestId;
        this.OwnerUsername = OwnerUsername;
        this.ApiKeyId = ApiKeyId;
        this.Payload = Payload;
        this.UpstreamRequest = UpstreamRequest;
        this.UpstreamResponse = UpstreamResponse;
        this.ResponsePayload = ResponsePayload;
        this.ErrorResponse = ErrorResponse;
        this.RequestModel = RequestModel;
        this.UpstreamModel = UpstreamModel;
        this.ChannelId = ChannelId;
        this.ChannelType = ChannelType;
        this.IsStream = IsStream;
        this.TtftMs = TtftMs;
        this.StatusCode = StatusCode;
        this.DurationMs = DurationMs;
        this.Error = Error;
        this.WebSearchDetails = WebSearchDetails;
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
}

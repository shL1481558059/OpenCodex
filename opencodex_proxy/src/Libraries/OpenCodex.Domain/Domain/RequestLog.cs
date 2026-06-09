namespace OpenCodex.Core.Domain;

public sealed class RequestLog : BaseEntity<long>
{
    public string? RequestId { get; set; }

    public double? CreatedAt { get; set; }

    public string? Method { get; set; }

    public string? Path { get; set; }

    public string? ClientIp { get; set; }

    public string? Model { get; set; }

    public string? UpstreamModel { get; set; }

    public string? ChannelId { get; set; }

    public string RequestType { get; set; } = "main";

    public long? ParentRequestLogId { get; set; }

    public bool IsStream { get; set; }

    public int? TtftMs { get; set; }

    public int? DurationMs { get; set; }

    public int? StatusCode { get; set; }

    public int InputTokens { get; set; }

    public int CachedTokens { get; set; }

    public int OutputTokens { get; set; }

    public double Cost { get; set; }

    public string OwnerUsername { get; set; } = "admin";

    public long? ApiKeyId { get; set; }

    public string? Error { get; set; }

    public RequestLogDetail? Detail { get; set; }

    public RequestLog? ParentRequestLog { get; set; }

    public ICollection<RequestLog> ChildRequestLogs { get; set; } = new List<RequestLog>();
}

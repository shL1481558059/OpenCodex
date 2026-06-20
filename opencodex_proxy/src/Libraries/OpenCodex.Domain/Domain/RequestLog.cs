namespace OpenCodex.Core.Domain;

public sealed class RequestLog : BaseEntity<Guid>
{
    public string? RequestId { get; set; }

    public double? CreatedAt { get; set; }

    public double? ProcessingStartedAt { get; set; }

    public double? CompletedAt { get; set; }

    public string? Method { get; set; }

    public string? Path { get; set; }

    public string? ClientIp { get; set; }

    public string? Model { get; set; }

    public string? UpstreamModel { get; set; }

    public Guid? ChannelId { get; set; }

    public string RequestType { get; set; } = "main";

    public string? LifecycleStatus { get; set; }

    public Guid? ParentRequestLogId { get; set; }

    public bool IsStream { get; set; }

    public int? TtftMs { get; set; }

    public int? DurationMs { get; set; }

    public int? StatusCode { get; set; }

    public int InputTokens { get; set; }

    public int CachedTokens { get; set; }

    public int OutputTokens { get; set; }

    public double Cost { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid? ApiKeyId { get; set; }

    public string? Error { get; set; }
}

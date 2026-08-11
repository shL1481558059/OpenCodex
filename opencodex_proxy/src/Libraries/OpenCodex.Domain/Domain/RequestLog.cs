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

    /// <summary>
    /// 客户端会话/线程的稳定索引键，不参与正文恢复。
    /// </summary>
    public string? ConversationKey { get; set; }

    /// <summary>
    /// 客户端 turn 标识。编辑或分支时每个请求仍保持自己的不可变值。
    /// </summary>
    public string? ConversationTurnId { get; set; }

    /// <summary>
    /// 客户端窗口标识（如果请求提供）。窗口不等同于对话分支。
    /// </summary>
    public string? ConversationWindowId { get; set; }

    /// <summary>
    /// 原始请求中的 previous_response_id，保留增量会话的显式父引用。
    /// </summary>
    public string? PreviousResponseId { get; set; }

    public bool IsStream { get; set; }

    public int? TtftMs { get; set; }

    public int? DurationMs { get; set; }

    public int? StatusCode { get; set; }

    public int InputTokens { get; set; }

    public int CachedTokens { get; set; }

    public int CacheWriteTokens { get; set; }

    public int CacheReadTokens { get; set; }

    public int OutputTokens { get; set; }

    public double Cost { get; set; }

    public string CostCurrency { get; set; } = "USD";

    public Guid? PricingModelInfoId { get; set; }

    public Guid? PricingPlanId { get; set; }

    public string? PricingSnapshotJson { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid? ApiKeyId { get; set; }

    public string? Error { get; set; }
}

namespace OpenCodex.Core.Domain;

public sealed class WebSearchContinuationEntry : BaseEntity<Guid>
{
    public Guid OwnerUserId { get; set; }

    public string EntryKey { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public int PayloadVersion { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public double CreatedAt { get; set; }
}

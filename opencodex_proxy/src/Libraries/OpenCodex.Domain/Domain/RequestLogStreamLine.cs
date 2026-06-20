namespace OpenCodex.Core.Domain;

public sealed class RequestLogStreamLine : BaseEntity<Guid>
{
    public Guid RequestLogId { get; set; }

    public int Sequence { get; set; }

    public double OccurredAt { get; set; }

    public string Source { get; set; } = "upstream";

    public string RawLine { get; set; } = string.Empty;
}

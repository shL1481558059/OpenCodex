namespace OpenCodex.Core.Domain;

public sealed class RequestLogStreamLine : BaseEntity<long>
{
    public long RequestLogId { get; set; }

    public int Sequence { get; set; }

    public double OccurredAt { get; set; }

    public string Source { get; set; } = "upstream";

    public string RawLine { get; set; } = string.Empty;

    public RequestLog? RequestLog { get; set; }
}

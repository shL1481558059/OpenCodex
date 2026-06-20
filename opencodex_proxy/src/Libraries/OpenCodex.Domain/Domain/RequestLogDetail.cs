namespace OpenCodex.Core.Domain;

public sealed class RequestLogDetail : BaseEntity<Guid>
{
    public Guid RequestLogId { get; set; }

    public string? RequestHeaders { get; set; }

    public string? RequestBody { get; set; }

    public string? UpstreamRequestBody { get; set; }

    public string? UpstreamResponseBody { get; set; }

    public string? ResponseBody { get; set; }

    public string? WebSearchJson { get; set; }

    public string? OcrJson { get; set; }

    public string? StreamTimingsJson { get; set; }
}

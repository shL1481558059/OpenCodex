namespace OpenCodex.Core.Domain;

public sealed class RequestLogDetail : BaseEntity
{
    public long RequestLogId { get; set; }

    public string? RequestHeaders { get; set; }

    public string? RequestBody { get; set; }

    public string? UpstreamRequestBody { get; set; }

    public string? UpstreamResponseBody { get; set; }

    public string? ResponseBody { get; set; }

    public string? WebSearchJson { get; set; }

    public string? OcrJson { get; set; }

    public string? StreamTimingsJson { get; set; }

    public RequestLog? RequestLog { get; set; }

    public override object? GetId()
    {
        return RequestLogId == 0 ? null : RequestLogId;
    }
}

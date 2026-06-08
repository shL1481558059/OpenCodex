namespace OpenCodex.CoreBase.Services.WebSearch;

public sealed class WebSearchStreamResult
{
    public Dictionary<string, object?>? FinalUpstreamRequest { get; set; }

    public Dictionary<string, object?>? FinalUpstreamResponse { get; set; }

    public Dictionary<string, object?>? ResponsePayload { get; set; }

    public Dictionary<string, object?>? Details { get; set; }
}

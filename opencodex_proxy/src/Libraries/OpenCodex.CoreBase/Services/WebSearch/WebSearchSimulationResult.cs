namespace OpenCodex.CoreBase.Services.WebSearch;

public sealed class WebSearchSimulationResult
{
    public WebSearchSimulationResult(
        Dictionary<string, object?> finalUpstreamRequest,
        Dictionary<string, object?>? finalUpstreamResponse,
        Dictionary<string, object?> responsePayload,
        Dictionary<string, object?> details)
    {
        FinalUpstreamRequest = finalUpstreamRequest;
        FinalUpstreamResponse = finalUpstreamResponse;
        ResponsePayload = responsePayload;
        Details = details;
    }

    public Dictionary<string, object?> FinalUpstreamRequest { get; set; }

    public Dictionary<string, object?>? FinalUpstreamResponse { get; set; }

    public Dictionary<string, object?> ResponsePayload { get; set; }

    public Dictionary<string, object?> Details { get; set; }
}

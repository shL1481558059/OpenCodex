namespace OpenCodex.Api.Services;

public sealed record WebSearchSimulationResult(
    Dictionary<string, object?> FinalUpstreamRequest,
    Dictionary<string, object?>? FinalUpstreamResponse,
    Dictionary<string, object?> ResponsePayload,
    Dictionary<string, object?> Details);

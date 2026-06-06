using OpenCodex.Api.Errors;

namespace OpenCodex.Api.Services;

public sealed class WebSearchSimulationUpstreamException : Exception
{
    public WebSearchSimulationUpstreamException(
        ProxyException proxyException,
        Dictionary<string, object?> finalUpstreamRequest,
        Dictionary<string, object?> details)
        : base(proxyException.Message, proxyException)
    {
        ProxyException = proxyException;
        FinalUpstreamRequest = finalUpstreamRequest;
        Details = details;
    }

    public ProxyException ProxyException { get; }

    public Dictionary<string, object?> FinalUpstreamRequest { get; }

    public Dictionary<string, object?> Details { get; }
}

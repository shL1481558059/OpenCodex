using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Protocols;
using OpenCodex.Api.Services.WebSearch;

namespace OpenCodex.Api.Services;

public sealed partial class WebSearchSimulator : IWebSearchSimulator
{
    private const string WebSearchToolName = WebSearchRequestPolicy.ToolName;

    private readonly IUpstreamClient _upstream;
    private readonly IWebSearchClient _webSearchClient;
    private readonly IProxyWebSearchRepository _webSearch;

    public WebSearchSimulator(
        IUpstreamClient upstream,
        IWebSearchClient webSearchClient,
        IProxyWebSearchRepository webSearch)
    {
        _upstream = upstream;
        _webSearchClient = webSearchClient;
        _webSearch = webSearch;
    }

    public bool CanSimulate(
        string entryProtocol,
        string channelType,
        string ownerRole,
        IReadOnlyDictionary<string, object?> payload)
    {
        return entryProtocol == ProtocolConverter.Responses
            && channelType is ProtocolConverter.Chat or ProtocolConverter.Messages
            && string.Equals(ownerRole, "superadmin", StringComparison.Ordinal)
            && WebSearchRequestPolicy.DeclaresWebSearchTool(payload)
            && _webSearch.ReadWebSearchConfig().Enabled;
    }

    private async Task<Dictionary<string, object?>> PostUpstream(
        IReadOnlyDictionary<string, object?> channel,
        Dictionary<string, object?> requestPayload,
        int defaultTimeout,
        IReadOnlyList<WebSearchToolResult> webResults,
        IReadOnlyList<Dictionary<string, object?>> upstreamCalls,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _upstream.PostJsonAsync(channel, requestPayload, defaultTimeout, cancellationToken);
        }
        catch (ProxyException exception)
        {
            var details = WebSearchSimulationLog.Build(webResults, upstreamCalls);
            details["upstream_error"] = exception.Message;
            throw new WebSearchSimulationUpstreamException(exception, requestPayload, details);
        }
    }
}

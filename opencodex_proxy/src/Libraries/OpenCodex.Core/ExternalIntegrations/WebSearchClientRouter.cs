using OpenCodex.CoreBase.Abstractions;
using OpenCodex.Core.Errors;
using OpenCodex.ProtocolConversion;
using OpenCodex.CoreBase.Domain.WebSearch;

namespace OpenCodex.Core.ExternalIntegrations;

public sealed class WebSearchClientRouter : IWebSearchClient
{
    private readonly IWebSearchClient _tavilyClient;
    private readonly IWebSearchClient _keenableClient;

    public WebSearchClientRouter(
        IWebSearchClient tavilyClient,
        IWebSearchClient keenableClient)
    {
        _tavilyClient = tavilyClient;
        _keenableClient = keenableClient;
    }

    public Task<WebSearchProviderResult> SearchAsync(
        WebSearchProviderKey key,
        string query,
        CancellationToken cancellationToken)
        => SearchAsync(key, query, WebSearchExecutionOptions.Default, cancellationToken);

    public async Task<WebSearchProviderResult> SearchAsync(
        WebSearchProviderKey key, string query, WebSearchExecutionOptions options, CancellationToken cancellationToken)
    {
        var provider = key.Provider.Trim().ToLowerInvariant();
        var result = provider switch
        {
            "tavily" => await _tavilyClient.SearchAsync(key, query, options, cancellationToken),
            "keenable" => await _keenableClient.SearchAsync(key, query, options, cancellationToken),
            _ => new WebSearchProviderResult(false, null, 0, "unsupported_provider",
                $"unsupported web search provider: {provider}",
                new WebSearchSummary(string.Empty, [], $"unsupported web search provider: {provider}"), null)
        };
        // No policy failure becomes an ordinary failed provider result or another-provider fallback.
        if (result.ErrorType is BoundedJsonResponseReader.CaptureLimitCode
            or BoundedJsonResponseReader.InvalidJsonCode or "response_capture_limit_exceeded")
        {
            if (UpstreamJsonEvidence.IsEvidence(result.Raw))
                throw new UpstreamException("Tool-provider JSON policy rejected the response.",
                    body: result.Raw, channelId: provider, errorCode: result.ErrorType);
            throw BoundedJsonResponseReader.ParsedFailure(ProtocolConversionParameters.Current.SseMaxCaptureBytes,
                result.ErrorType!, "Tool-provider JSON policy rejected a parsed result without wire evidence.",
                "provider_policy_result", provider);
        }
        // Backstop for an injected/custom client: parsed provenance is explicit, never fake received bytes.
        var captureMaxBytes = ProtocolConversionParameters.Current.SseMaxCaptureBytes;
        BoundedJsonResponseReader.EnsureParsedBound(result.Raw, captureMaxBytes, cancellationToken, "provider_raw", provider);
        BoundedJsonResponseReader.EnsureParsedBound(result.Summary, captureMaxBytes, cancellationToken, "provider_summary", provider);
        return result;
    }
}

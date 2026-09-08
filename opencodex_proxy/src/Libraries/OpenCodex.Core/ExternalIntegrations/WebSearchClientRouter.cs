using OpenCodex.CoreBase.Abstractions;

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
    {
        var provider = key.Provider.Trim().ToLowerInvariant();
        return provider switch
        {
            "tavily" => _tavilyClient.SearchAsync(key, query, cancellationToken),
            "keenable" => _keenableClient.SearchAsync(key, query, cancellationToken),
            _ => Task.FromResult(new WebSearchProviderResult(
                false,
                null,
                0,
                "unsupported_provider",
                $"unsupported web search provider: {provider}",
                new WebSearchSummary(string.Empty, [], $"unsupported web search provider: {provider}"),
                null))
        };
    }
}

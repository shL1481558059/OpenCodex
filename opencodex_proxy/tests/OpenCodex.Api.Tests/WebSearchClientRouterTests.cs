using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class WebSearchClientRouterTests
{
    [Fact]
    public async Task SearchAsync_RoutesKeenableToKeenableClient()
    {
        var router = new WebSearchClientRouter(
            new StubWebSearchClient("tavily"),
            new StubWebSearchClient("keenable"));

        var result = await router.SearchAsync(
            new WebSearchProviderKey("keenable", "keen_secret"),
            "query",
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("keenable", result.Summary.Answer);
    }

    [Fact]
    public async Task SearchAsync_RoutesTavilyToTavilyClient()
    {
        var router = new WebSearchClientRouter(
            new StubWebSearchClient("tavily"),
            new StubWebSearchClient("keenable"));

        var result = await router.SearchAsync(
            new WebSearchProviderKey("tavily", "tvly_secret"),
            "query",
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("tavily", result.Summary.Answer);
    }

    [Fact]
    public async Task SearchAsync_UnknownProvider_ReturnsFailure()
    {
        var router = new WebSearchClientRouter(
            new StubWebSearchClient("tavily"),
            new StubWebSearchClient("keenable"));

        var result = await router.SearchAsync(
            new WebSearchProviderKey("unknown", "secret"),
            "query",
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("unsupported_provider", result.ErrorType);
        Assert.Contains("unsupported web search provider: unknown", result.Error);
    }

    [Fact]
    public async Task SearchAsync_ForwardsExecutionOptionsToTheSelectedClient()
    {
        var tavily = new StubWebSearchClient("tavily");
        var keenable = new StubWebSearchClient("keenable");
        var router = new WebSearchClientRouter(tavily, keenable);
        var options = new WebSearchExecutionOptions(WebSearchExecutionOptions.High, ["example.com"]);

        var result = await router.SearchAsync(
            new WebSearchProviderKey(" Tavily ", "tvly_secret"),
            "query",
            options,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Same(options, tavily.LastOptions);
        Assert.Null(keenable.LastOptions);
    }

    private sealed class StubWebSearchClient(string name) : IWebSearchClient
    {
        public WebSearchExecutionOptions? LastOptions { get; private set; }

        public Task<WebSearchProviderResult> SearchAsync(
            WebSearchProviderKey key,
            string query,
            CancellationToken cancellationToken)
            => SearchAsync(key, query, WebSearchExecutionOptions.Default, cancellationToken);

        public Task<WebSearchProviderResult> SearchAsync(
            WebSearchProviderKey key,
            string query,
            WebSearchExecutionOptions options,
            CancellationToken cancellationToken)
        {
            LastOptions = options;
            return Task.FromResult(new WebSearchProviderResult(
                true,
                200,
                0,
                null,
                null,
                new WebSearchSummary(name, [], null),
                null));
        }
    }
}

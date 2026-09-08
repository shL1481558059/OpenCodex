using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.CoreBase.Abstractions;
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

    private sealed class StubWebSearchClient(string name) : IWebSearchClient
    {
        public Task<WebSearchProviderResult> SearchAsync(
            WebSearchProviderKey key,
            string query,
            CancellationToken cancellationToken)
        {
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

using System.Net;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class TavilyWebSearchClientTests
{
    [Fact]
    public async Task SearchAsync_MediumOmitsChunksAndDomainFilters()
    {
        var handler = new CaptureHandler(_ => Ok(
            """
            {"answer":"A guide","results":[{"title":"Docs","url":"https://example.test","content":"Use strict mode","score":0.9}]}
            """));
        var client = new TavilyWebSearchClient(new HttpClient(handler));

        var result = await client.SearchAsync(
            new WebSearchProviderKey("tavily", "tvly_secret"),
            "typescript",
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Bearer tvly_secret", handler.Authorization);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("typescript", json.RootElement.GetProperty("query").GetString());
        Assert.Equal("basic", json.RootElement.GetProperty("search_depth").GetString());
        Assert.Equal(5, json.RootElement.GetProperty("max_results").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("chunks_per_source", out _));
        Assert.False(json.RootElement.TryGetProperty("include_domains", out _));
        Assert.False(json.RootElement.TryGetProperty("include_domains_mode", out _));
        var item = Assert.Single(result.Summary.Results);
        Assert.Equal("Docs", item["title"]);
        Assert.Equal("https://example.test", item["url"]);
        Assert.Equal("Use strict mode", item["content"]);
    }

    [Theory]
    [InlineData(WebSearchExecutionOptions.Low, 3, "basic", 1)]
    [InlineData(WebSearchExecutionOptions.High, 8, "advanced", 3)]
    public async Task SearchAsync_ContextSizeAndDomainsChangeThePayload(
        string size,
        int maxResults,
        string depth,
        int chunks)
    {
        var handler = new CaptureHandler(_ => Ok("""{"results":[]}"""));
        var client = new TavilyWebSearchClient(new HttpClient(handler));

        await client.SearchAsync(
            new WebSearchProviderKey("tavily", "tvly_secret"),
            "query",
            new WebSearchExecutionOptions(size, ["example.com", "docs.example.com"]),
            CancellationToken.None);

        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(maxResults, json.RootElement.GetProperty("max_results").GetInt32());
        Assert.Equal(depth, json.RootElement.GetProperty("search_depth").GetString());
        Assert.Equal(chunks, json.RootElement.GetProperty("chunks_per_source").GetInt32());
        Assert.Equal("restrict", json.RootElement.GetProperty("include_domains_mode").GetString());
        Assert.Equal(
            ["example.com", "docs.example.com"],
            json.RootElement.GetProperty("include_domains").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public async Task SearchAsync_TrimsResultsToTheRequestedCap()
    {
        var results = string.Join(
            ",",
            Enumerable.Range(0, 6).Select(index =>
                "{\"title\":\"S" + index + "\",\"url\":\"https://example.test/" + index + "\",\"content\":\"c\"}"));
        var handler = new CaptureHandler(_ => Ok("{\"results\":[" + results + "]}"));
        var client = new TavilyWebSearchClient(new HttpClient(handler));

        var result = await client.SearchAsync(
            new WebSearchProviderKey("tavily", "tvly_secret"),
            "query",
            new WebSearchExecutionOptions(WebSearchExecutionOptions.Low, []),
            CancellationToken.None);

        Assert.Equal(3, result.Summary.Results.Count);
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.TryGetValues("authorization", out var values) ? values.SingleOrDefault() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return response(request);
        }
    }
}

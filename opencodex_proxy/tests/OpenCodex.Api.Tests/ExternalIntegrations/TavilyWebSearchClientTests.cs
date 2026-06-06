using System.Net;
using System.Text.Json;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.ExternalIntegrations;

namespace OpenCodex.Api.Tests.ExternalIntegrations;

public sealed class TavilyWebSearchClientTests
{
    [Fact]
    public async Task SearchAsyncBuildsRequestAndMapsSuccessfulSummaryWithoutTrimmingStrings()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "answer": "  answer  ",
              "results": [
                {
                  "title": "  title  ",
                  "url": " https://example.test ",
                  "content": "  content  ",
                  "score": 0.75
                },
                "ignored"
              ],
              "usage": {"credits": 1}
            }
            """)
        });
        var client = new TavilyWebSearchClient(new HttpClient(handler));

        var result = await client.SearchAsync(Key(), "OpenAI", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Error);
        Assert.Equal("  answer  ", result.Summary.Answer);
        var summaryResult = Assert.Single(result.Summary.Results);
        Assert.Equal("  title  ", summaryResult["title"]);
        Assert.Equal(" https://example.test ", summaryResult["url"]);
        Assert.Equal("  content  ", summaryResult["content"]);
        Assert.Equal(0.75, summaryResult["score"]);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.tavily.com/search", request.RequestUri?.ToString());
        Assert.Equal("Bearer tvly-secret", Assert.Single(request.Headers.GetValues("authorization")));
        var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies)).RootElement;
        Assert.Equal("OpenAI", payload.GetProperty("query").GetString());
        Assert.Equal("basic", payload.GetProperty("search_depth").GetString());
        Assert.Equal(5, payload.GetProperty("max_results").GetInt32());
        Assert.True(payload.GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task SearchAsyncMapsHttpErrorJsonBodyToRawPayload()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"rate limit"}""")
        });
        var client = new TavilyWebSearchClient(new HttpClient(handler));

        var result = await client.SearchAsync(Key(), "OpenAI", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal("http_error", result.ErrorType);
        Assert.Equal("Tavily returned HTTP 429", result.Error);
        var raw = Assert.IsType<Dictionary<string, object?>>(result.Raw);
        Assert.Equal("rate limit", raw["error"]);
        Assert.Equal("Tavily returned HTTP 429", result.Summary.Error);
    }

    private static WebSearchProviderKey Key()
    {
        return new WebSearchProviderKey(
            "tavily",
            "tvly-secret");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return _handler(request);
        }
    }
}

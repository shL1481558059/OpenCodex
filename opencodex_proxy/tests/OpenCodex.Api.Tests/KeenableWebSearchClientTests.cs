using System.Net;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class KeenableWebSearchClientTests
{
    [Fact]
    public async Task SearchAsync_SendsApiKeyHeaderAndMapsResults()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "query": "typescript best practices",
                  "results": [
                    {
                      "title": "TypeScript Best Practices",
                      "url": "https://example.test/typescript",
                      "description": "A guide",
                      "snippet": "Use strict mode",
                      "published_at": "2026-01-15T10:30:00Z",
                      "acquired_at": "2026-01-16T08:12:34Z"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        var client = new KeenableWebSearchClient(new HttpClient(handler));

        var result = await client.SearchAsync(
            new WebSearchProviderKey("keenable", "keen_secret"),
            "typescript best practices",
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal("https://api.keenable.ai/v1/search", handler.Uri);
        Assert.Equal("keen_secret", handler.ApiKey);
        using (var json = JsonDocument.Parse(handler.Body!))
        {
            Assert.Equal("typescript best practices", json.RootElement.GetProperty("query").GetString());
            Assert.Equal(5, json.RootElement.GetProperty("max_results").GetInt32());
            Assert.Equal(["query", "max_results"], json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        }

        var item = Assert.Single(result.Summary.Results);
        Assert.Equal("TypeScript Best Practices", item["title"]);
        Assert.Equal("https://example.test/typescript", item["url"]);
        Assert.Equal("Use strict mode", item["content"]);
        Assert.Equal("A guide", item["description"]);
    }

    [Fact]
    public async Task SearchAsync_HttpError_ReturnsFailureWithRawBody()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"Rate limit exceeded"}""", Encoding.UTF8, "application/json")
        });
        var client = new KeenableWebSearchClient(new HttpClient(handler));

        var result = await client.SearchAsync(
            new WebSearchProviderKey("keenable", "keen_secret"),
            "typescript best practices",
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal("http_error", result.ErrorType);
        Assert.Contains("Keenable returned HTTP 429", result.Error);
        var raw = Assert.IsType<Dictionary<string, object?>>(result.Raw);
        Assert.Equal("Rate limit exceeded", raw["error"]);
    }

    [Fact]
    public async Task SearchAsync_InvalidJson_ReturnsRequestError()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json")
        });
        var client = new KeenableWebSearchClient(new HttpClient(handler));

        var result = await client.SearchAsync(
            new WebSearchProviderKey("keenable", "keen_secret"),
            "typescript best practices",
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Null(result.StatusCode);
        Assert.Equal("request_error", result.ErrorType);
        Assert.Equal("Keenable returned invalid JSON", result.Error);
    }

    [Theory]
    [InlineData(WebSearchExecutionOptions.Low, 3, 500)]
    [InlineData(WebSearchExecutionOptions.High, 10, 2000)]
    public async Task SearchAsync_ContextSizeChangesResultAndSnippetLimits(string size, int maxResults, int snippetLength)
    {
        var handler = new CaptureHandler(_ => Ok("""{"results":[]}"""));
        var client = new KeenableWebSearchClient(new HttpClient(handler));

        await client.SearchAsync(
            new WebSearchProviderKey("keenable", "keen_secret"),
            "query",
            new WebSearchExecutionOptions(size, ["example.com"]),
            CancellationToken.None);

        Assert.Equal(1, handler.SendCount);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(maxResults, json.RootElement.GetProperty("max_results").GetInt32());
        Assert.Equal(snippetLength, json.RootElement.GetProperty("snippet_max_length").GetInt32());
        Assert.Equal("example.com", json.RootElement.GetProperty("site").GetString());
    }

    [Fact]
    public async Task SearchAsync_MultipleDomains_MergesDedupedResultsUpToTheCap()
    {
        var handler = new CaptureHandler(request =>
        {
            var site = Site(request);
            var first = site == "a.example" ? "https://a.example/1" : "https://b.example/1";
            var shared = "https://shared.example/same";
            return Ok(
                $$"""
                {"answer":"from {{site}}","results":[
                  {"title":"{{site}}","url":"{{first}}","snippet":"one"},
                  {"title":"shared","url":"{{shared}}","snippet":"same"},
                  {"title":"extra","url":"https://{{site}}/extra","snippet":"extra"}
                ]}
                """);
        });
        var client = new KeenableWebSearchClient(new HttpClient(handler));

        var result = await client.SearchAsync(
            new WebSearchProviderKey("keenable", "keen_secret"),
            "query",
            new WebSearchExecutionOptions(WebSearchExecutionOptions.Low, ["a.example", "b.example"]),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, handler.SendCount);
        Assert.Equal(["a.example", "b.example"], handler.Bodies.Select(SiteFromBody).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, result.Summary.Results.Count);
        Assert.Equal(
            ["https://a.example/1", "https://shared.example/same", "https://a.example/extra"],
            result.Summary.Results.Select(item => item["url"]).ToArray());
        Assert.Equal("from a.example", result.Summary.Answer);
    }

    [Fact]
    public async Task SearchAsync_PartialDomainFailure_ReturnsSuccessfulDomains()
    {
        var handler = new CaptureHandler(request =>
        {
            if (Site(request) == "bad.example")
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("""{"error":"down"}""", Encoding.UTF8, "application/json")
                };
            }

            return Ok("""{"results":[{"title":"ok","url":"https://ok.example","snippet":"yes"}]}""");
        });
        var client = new KeenableWebSearchClient(new HttpClient(handler));

        var result = await client.SearchAsync(
            new WebSearchProviderKey("keenable", "keen_secret"),
            "query",
            new WebSearchExecutionOptions(WebSearchExecutionOptions.Medium, ["bad.example", "ok.example"]),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
        var item = Assert.Single(result.Summary.Results);
        Assert.Equal("https://ok.example", item["url"]);
    }

    [Fact]
    public async Task SearchAsync_AllDomainsFail_ReturnsFailure()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"Rate limit exceeded"}""", Encoding.UTF8, "application/json")
        });
        var client = new KeenableWebSearchClient(new HttpClient(handler));

        var result = await client.SearchAsync(
            new WebSearchProviderKey("keenable", "keen_secret"),
            "query",
            new WebSearchExecutionOptions(WebSearchExecutionOptions.Medium, ["a.example", "b.example"]),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal("http_error", result.ErrorType);
        Assert.Contains("failed for all 2 domains", result.Error);
    }

    [Fact]
    public async Task SearchAsync_MultipleDomains_LimitsConcurrency()
    {
        var handler = new ConcurrentHandler();
        var client = new KeenableWebSearchClient(new HttpClient(handler));
        var domains = Enumerable.Range(0, 6).Select(index => $"d{index}.example").ToArray();

        var result = await client.SearchAsync(
            new WebSearchProviderKey("keenable", "keen_secret"),
            "query",
            new WebSearchExecutionOptions(WebSearchExecutionOptions.Medium, domains),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(6, handler.SendCount);
        Assert.InRange(handler.MaxInFlight, 2, WebSearchProviderProfile.KeenableDomainConcurrency);
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string Site(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return SiteFromBody(body);
    }

    private static string SiteFromBody(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("site").GetString()!;
    }

    private sealed class ConcurrentHandler : HttpMessageHandler
    {
        private int _inFlight;
        private int _sendCount;
        private int _maxInFlight;

        public int SendCount => _sendCount;

        public int MaxInFlight => _maxInFlight;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _inFlight);
            Interlocked.Increment(ref _sendCount);
            int observed;
            do
            {
                observed = _maxInFlight;
                if (current <= observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref _maxInFlight, current, observed) != observed);

            try
            {
                await Task.Delay(40, cancellationToken);
                return Ok("""{"results":[]}""");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        public string? Uri { get; private set; }

        public string? ApiKey { get; private set; }

        public string? Body { get; private set; }

        public IReadOnlyList<string> Bodies => _bodies;

        private readonly List<string> _bodies = [];

        private readonly object _gate = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                SendCount++;
                Uri = request.RequestUri?.ToString();
                ApiKey = request.Headers.TryGetValues("x-api-key", out var values) ? values.SingleOrDefault() : null;
                Body = body;
                if (body is not null)
                {
                    _bodies.Add(body);
                }
            }

            return response(request);
        }
    }
}

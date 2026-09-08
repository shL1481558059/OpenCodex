using System.Net;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.CoreBase.Abstractions;
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

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        public string? Uri { get; private set; }

        public string? ApiKey { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            Uri = request.RequestUri?.ToString();
            ApiKey = request.Headers.TryGetValues("x-api-key", out var values) ? values.SingleOrDefault() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return response(request);
        }
    }
}

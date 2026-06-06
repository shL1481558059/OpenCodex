using System.Net;
using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Errors;
using OpenCodex.Api.ExternalIntegrations;
using OpenCodex.Api.Protocols;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.ExternalIntegrations;

public sealed class HttpUpstreamClientTests
{
    [Fact]
    public async Task PostJsonAsyncReturnsParsedJsonObject()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"resp_1","count":2,"ok":true}""")
        });
        var client = Client(handler);

        var result = await client.PostJsonAsync(
            Channel(),
            new Dictionary<string, object?> { ["model"] = "gpt-5", ["input"] = "ping" },
            60,
            CancellationToken.None);

        Assert.Equal("resp_1", result["id"]);
        Assert.Equal(2L, Convert.ToInt64(result["count"]));
        Assert.True((bool)result["ok"]!);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("https://upstream.test/v1/responses", handler.Requests[0].RequestUri?.ToString());
    }

    [Fact]
    public async Task PostJsonAsyncRejectsInvalidJsonObject()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[1,2,3]")
        });
        var client = Client(handler);

        var exception = await Assert.ThrowsAsync<UpstreamException>(() =>
            client.PostJsonAsync(
                Channel(),
                new Dictionary<string, object?> { ["model"] = "gpt-5", ["input"] = "ping" },
                60,
                CancellationToken.None));

        Assert.Equal("upstream returned invalid JSON", exception.Message);
        Assert.Equal(StatusCodes.Status502BadGateway, exception.StatusCode);
        Assert.Equal("primary", exception.ChannelId);
        Assert.Null(exception.Body);
    }

    [Fact]
    public async Task PostJsonAsyncDecodesHttpErrorJsonBody()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"bad request"},"retryable":false}""")
        });
        var client = Client(handler);

        var exception = await Assert.ThrowsAsync<UpstreamException>(() =>
            client.PostJsonAsync(
                Channel(),
                new Dictionary<string, object?> { ["model"] = "gpt-5", ["input"] = "ping" },
                60,
                CancellationToken.None));

        Assert.Equal("upstream returned HTTP 400", exception.Message);
        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal("primary", exception.ChannelId);
        var body = Assert.IsType<Dictionary<string, object?>>(exception.Body);
        var error = Assert.IsType<Dictionary<string, object?>>(body["error"]);
        Assert.Equal("bad request", error["message"]);
        Assert.False((bool)body["retryable"]!);
    }

    [Fact]
    public async Task PostJsonAsyncMapsHttpRequestException()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("dns failed"));
        var client = Client(handler);

        var exception = await Assert.ThrowsAsync<UpstreamException>(() =>
            client.PostJsonAsync(
                Channel(),
                new Dictionary<string, object?> { ["model"] = "gpt-5", ["input"] = "ping" },
                60,
                CancellationToken.None));

        Assert.Equal("failed to reach upstream: dns failed", exception.Message);
        Assert.Equal(StatusCodes.Status502BadGateway, exception.StatusCode);
        Assert.Equal("primary", exception.ChannelId);
        Assert.Null(exception.Body);
    }

    [Fact]
    public async Task ListModelsAsyncUsesModelsEndpoint()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"object":"list","data":[{"id":"gpt-5"}]}""")
        });
        var client = Client(handler);

        var result = await client.ListModelsAsync(Channel(), 60, CancellationToken.None);

        Assert.Equal("list", result["object"]);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("https://upstream.test/v1/models", handler.Requests[0].RequestUri?.ToString());
    }

    [Fact]
    public async Task PostJsonAsyncBuildsMessagesHeaders()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"msg_1"}""")
        });
        var client = Client(handler);

        await client.PostJsonAsync(
            Channel(ProtocolConverter.Messages),
            new Dictionary<string, object?> { ["model"] = "claude", ["messages"] = new List<object?>() },
            60,
            CancellationToken.None);

        var request = handler.Requests[0];
        Assert.Equal("https://upstream.test/v1/messages", request.RequestUri?.ToString());
        Assert.Equal("secret", Assert.Single(request.Headers.GetValues("x-api-key")));
        Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
        Assert.False(request.Headers.Contains("authorization"));
    }

    [Fact]
    public async Task PostJsonAsyncKeepsCustomAnthropicVersionHeader()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"msg_1"}""")
        });
        var client = Client(handler);
        var channel = Channel(ProtocolConverter.Messages);
        channel["headers"] = new Dictionary<string, object?>
        {
            ["anthropic-version"] = "2024-01-01"
        };

        await client.PostJsonAsync(
            channel,
            new Dictionary<string, object?> { ["model"] = "claude", ["messages"] = new List<object?>() },
            60,
            CancellationToken.None);

        var request = handler.Requests[0];
        Assert.Equal("2024-01-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
        Assert.Equal("secret", Assert.Single(request.Headers.GetValues("x-api-key")));
    }

    [Fact]
    public async Task PostJsonAsyncConfigAuthOverridesCustomAuthorizationHeader()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"chat_1"}""")
        });
        var client = Client(handler);
        var channel = Channel(ProtocolConverter.Chat);
        channel["headers"] = new Dictionary<string, object?>
        {
            ["authorization"] = "Bearer custom"
        };

        await client.PostJsonAsync(
            channel,
            new Dictionary<string, object?> { ["model"] = "gpt-5", ["messages"] = new List<object?>() },
            60,
            CancellationToken.None);

        var request = handler.Requests[0];
        Assert.Equal("Bearer secret", Assert.Single(request.Headers.GetValues("authorization")));
    }

    [Fact]
    public async Task PostJsonAsyncTrimsChannelConfigValues()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("""{"error":"bad gateway"}""")
        });
        var client = Client(handler);
        var channel = Channel(ProtocolConverter.Chat);
        channel["id"] = " primary ";
        channel["type"] = " chat ";
        channel["baseurl"] = " https://upstream.test/ ";
        channel["auth_mode"] = " config ";
        channel["apikey"] = " secret ";

        var exception = await Assert.ThrowsAsync<UpstreamException>(() =>
            client.PostJsonAsync(
                channel,
                new Dictionary<string, object?> { ["model"] = "gpt-5", ["messages"] = new List<object?>() },
                60,
                CancellationToken.None));

        var request = handler.Requests[0];
        Assert.Equal("https://upstream.test/v1/chat/completions", request.RequestUri?.ToString());
        Assert.Equal("Bearer secret", Assert.Single(request.Headers.GetValues("authorization")));
        Assert.Equal("primary", exception.ChannelId);
    }

    [Fact]
    public async Task PostJsonAsyncOmitsAuthorizationWhenAuthModeIsNotConfig()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"chat_1"}""")
        });
        var client = Client(handler);
        var channel = Channel(ProtocolConverter.Chat);
        channel["auth_mode"] = "none";

        await client.PostJsonAsync(
            channel,
            new Dictionary<string, object?> { ["model"] = "gpt-5", ["messages"] = new List<object?>() },
            60,
            CancellationToken.None);

        var request = handler.Requests[0];
        Assert.Equal("https://upstream.test/v1/chat/completions", request.RequestUri?.ToString());
        Assert.False(request.Headers.Contains("authorization"));
        Assert.False(request.Headers.Contains("x-api-key"));
    }

    [Fact]
    public async Task PostJsonAsyncTruncatesPlainTextHttpErrorBody()
    {
        var upstreamBody = new string('x', 2105);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(upstreamBody)
        });
        var client = Client(handler);

        var exception = await Assert.ThrowsAsync<UpstreamException>(() =>
            client.PostJsonAsync(
                Channel(),
                new Dictionary<string, object?> { ["model"] = "gpt-5", ["input"] = "ping" },
                60,
                CancellationToken.None));

        Assert.Equal("upstream returned HTTP 502", exception.Message);
        Assert.Equal(StatusCodes.Status502BadGateway, exception.StatusCode);
        var body = Assert.IsType<string>(exception.Body);
        Assert.Equal(2000, body.Length);
        Assert.Equal(new string('x', 2000), body);
    }

    [Fact]
    public async Task StreamJsonAsyncReadsLinesWithTrailingNewlines()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: first\n\ndata: [DONE]\n")
        });
        var client = Client(handler);

        var lines = await ReadStream(
            client.StreamJsonAsync(
                Channel(),
                new Dictionary<string, object?> { ["model"] = "gpt-5", ["stream"] = true },
                60,
                CancellationToken.None));

        Assert.Equal(["data: first\n", "\n", "data: [DONE]\n"], lines);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("https://upstream.test/v1/responses", handler.Requests[0].RequestUri?.ToString());
    }

    [Fact]
    public async Task StreamJsonAsyncDecodesHttpErrorJsonBody()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"invalid key"}}""")
        });
        var client = Client(handler);

        var exception = await Assert.ThrowsAsync<UpstreamException>(() =>
            ReadStream(
                client.StreamJsonAsync(
                    Channel(),
                    new Dictionary<string, object?> { ["model"] = "gpt-5", ["stream"] = true },
                    60,
                    CancellationToken.None)));

        Assert.Equal("upstream returned HTTP 401", exception.Message);
        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
        Assert.Equal("primary", exception.ChannelId);
        var body = Assert.IsType<Dictionary<string, object?>>(exception.Body);
        var error = Assert.IsType<Dictionary<string, object?>>(body["error"]);
        Assert.Equal("invalid key", error["message"]);
    }

    private static HttpUpstreamClient Client(HttpMessageHandler handler)
    {
        return new HttpUpstreamClient(new HttpClient(handler));
    }

    private static Dictionary<string, object?> Channel(string type = ProtocolConverter.Responses)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "primary",
            ["type"] = type,
            ["baseurl"] = "https://upstream.test",
            ["apikey"] = "secret",
            ["retry_count"] = 0
        };
    }

    private static async Task<List<string>> ReadStream(IAsyncEnumerable<string> lines)
    {
        var result = new List<string>();
        await foreach (var line in lines)
        {
            result.Add(line);
        }

        return result;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }
}

using System.Net;
using System.Text;
using OpenCodex.Core.ExternalIntegrations;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class NativeMcpHeaderTests
{
    [Fact]
    public async Task MessagesMcpRequest_AddsCurrentAnthropicBetaHeader()
    {
        var handler = new CaptureHandler();
        var client = new HttpUpstreamClient(new HttpClient(handler));
        await client.PostJsonAsync(
            new Dictionary<string, object?>
            {
                ["type"] = "messages",
                ["baseurl"] = "https://example.test/v1",
                ["apikey"] = "secret",
                ["auth_mode"] = "config",
                ["retry_count"] = 0
            },
            new Dictionary<string, object?>
            {
                ["model"] = "claude",
                ["max_tokens"] = 100,
                ["messages"] = new List<object?>(),
                ["mcp_servers"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "url",
                        ["name"] = "weather",
                        ["url"] = "https://mcp.example.test"
                    }
                }
            },
            30,
            CancellationToken.None);

        Assert.Equal("mcp-client-2025-11-20", handler.AnthropicBeta);
    }

    [Fact]
    public async Task NormalMessagesRequest_DoesNotAddMcpBetaHeader()
    {
        var handler = new CaptureHandler();
        var client = new HttpUpstreamClient(new HttpClient(handler));
        await client.PostJsonAsync(
            new Dictionary<string, object?>
            {
                ["type"] = "messages",
                ["baseurl"] = "https://example.test/v1",
                ["apikey"] = "secret",
                ["auth_mode"] = "config",
                ["retry_count"] = 0
            },
            new Dictionary<string, object?>
            {
                ["model"] = "claude",
                ["max_tokens"] = 100,
                ["messages"] = new List<object?>()
            },
            30,
            CancellationToken.None);

        Assert.Null(handler.AnthropicBeta);
    }

    [Fact]
    public async Task MessagesMcpRequest_MergesCurrentMcpBetaWithExistingBeta()
    {
        var handler = new CaptureHandler();
        var client = new HttpUpstreamClient(new HttpClient(handler));
        await client.PostJsonAsync(
            MessagesChannel("prompt-caching-2024-07-31"),
            MessagesMcpPayload(),
            30,
            CancellationToken.None);

        var betaValues = Assert.IsType<string>(handler.AnthropicBeta)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("prompt-caching-2024-07-31", betaValues);
        Assert.Contains("mcp-client-2025-11-20", betaValues);
    }

    [Fact]
    public async Task MessagesMcpRequest_DeduplicatesCurrentMcpBeta()
    {
        var handler = new CaptureHandler();
        var client = new HttpUpstreamClient(new HttpClient(handler));
        await client.PostJsonAsync(
            MessagesChannel("mcp-client-2025-11-20, mcp-client-2025-11-20"),
            MessagesMcpPayload(),
            30,
            CancellationToken.None);

        var betaValues = Assert.IsType<string>(handler.AnthropicBeta)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(1, betaValues.Count(value => value == "mcp-client-2025-11-20"));
    }

    private static Dictionary<string, object?> MessagesChannel(string anthropicBeta)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "messages",
            ["baseurl"] = "https://example.test/v1",
            ["apikey"] = "secret",
            ["auth_mode"] = "config",
            ["retry_count"] = 0,
            ["headers"] = new Dictionary<string, object?> { ["anthropic-beta"] = anthropicBeta }
        };
    }

    private static Dictionary<string, object?> MessagesMcpPayload()
    {
        return new Dictionary<string, object?>
        {
            ["model"] = "claude",
            ["max_tokens"] = 100,
            ["messages"] = new List<object?>(),
            ["mcp_servers"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "url",
                    ["name"] = "weather",
                    ["url"] = "https://mcp.example.test"
                }
            }
        };
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? AnthropicBeta { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AnthropicBeta = request.Headers.TryGetValues("anthropic-beta", out var values)
                ? values.Single()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}

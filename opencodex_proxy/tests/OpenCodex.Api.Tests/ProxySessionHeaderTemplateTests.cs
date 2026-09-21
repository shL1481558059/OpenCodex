using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxySessionHeaderTemplateTests
{
    [Fact]
    public void ResolveSessionId_PrefersThreadMetadataOverSessionHeaders()
    {
        var request = Request(
            new Dictionary<string, string>
            {
                ["x-codex-turn-metadata"] =
                    """{"thread_id":"thread-1","session_id":"session-1"}""",
                ["thread-id"] = "header-thread",
                ["session-id"] = "header-session"
            });

        var sessionId = ProxySessionHeaderTemplate.ResolveSessionId(
            request,
            new Dictionary<string, object?>());

        Assert.Equal("thread-1", sessionId);
    }

    [Fact]
    public void ResolveSessionId_UsesPromptCacheKeyWhenHeadersDoNotProvideIdentity()
    {
        var request = Request(new Dictionary<string, string>());
        var payload = new Dictionary<string, object?>
        {
            ["prompt_cache_key"] = "cache-key-1"
        };

        var sessionId = ProxySessionHeaderTemplate.ResolveSessionId(request, payload);

        Assert.Equal("cache-key-1", sessionId);
    }

    [Fact]
    public void ResolveSessionId_UsesInteractionIdAsSessionFallback()
    {
        var request = Request(new Dictionary<string, string>
        {
            ["User-Agent"] = "GitHubCopilotChat/0.65.0",
            ["X-Interaction-Id"] = "6e55489b-a9f3-4bf1-abc5-69dfdf5f3108"
        });

        var sessionId = ProxySessionHeaderTemplate.ResolveSessionId(
            request,
            new Dictionary<string, object?>());

        Assert.Equal("6e55489b-a9f3-4bf1-abc5-69dfdf5f3108", sessionId);
    }

    [Fact]
    public void ResolveSessionId_PrefersExplicitSessionHeaderOverInteractionId()
    {
        var request = Request(new Dictionary<string, string>
        {
            ["session-id"] = "explicit-session",
            ["x-interaction-id"] = "interaction-session"
        });

        var sessionId = ProxySessionHeaderTemplate.ResolveSessionId(
            request,
            new Dictionary<string, object?>());

        Assert.Equal("explicit-session", sessionId);
    }

    [Fact]
    public void ResolveSessionId_NoIdentity_CreatesRandomOpenCodeSessionId()
    {
        var sessionId = ProxySessionHeaderTemplate.ResolveSessionId(
            Request(new Dictionary<string, string>()),
            new Dictionary<string, object?>());

        Assert.Matches("^ses_[0-9a-f]{32}$", sessionId);
    }

    [Fact]
    public void ResolveSessionId_InvalidIdentity_FallsBackToRandomOpenCodeSessionId()
    {
        var request = Request(new Dictionary<string, string>
        {
            ["session-id"] = "invalid\r\nvalue"
        });

        var sessionId = ProxySessionHeaderTemplate.ResolveSessionId(
            request,
            new Dictionary<string, object?>());

        Assert.Matches("^ses_[0-9a-f]{32}$", sessionId);
    }

    [Fact]
    public void ApplyToChannel_ReplacesAllOccurrencesWithoutMutatingSource()
    {
        var sourceHeaders = new Dictionary<string, object?>
        {
            ["x-opencode-session"] = "{{session_id}}",
            ["x-related-session"] = "prefix-{{session_id}}-suffix"
        };
        var channel = new Dictionary<string, object?>
        {
            ["type"] = "chat",
            ["headers"] = sourceHeaders
        };

        var resolved = ProxySessionHeaderTemplate.ApplyToChannel(channel, "ses_abc");

        var headers = Assert.IsType<Dictionary<string, object?>>(resolved["headers"]);
        Assert.Equal("ses_abc", headers["x-opencode-session"]);
        Assert.Equal("prefix-ses_abc-suffix", headers["x-related-session"]);
        Assert.Equal("{{session_id}}", sourceHeaders["x-opencode-session"]);
        Assert.Equal("prefix-{{session_id}}-suffix", sourceHeaders["x-related-session"]);
    }

    [Fact]
    public void Apply_NoPlaceholder_ReturnsOriginalRoute()
    {
        var route = Route(new Dictionary<string, object?>
        {
            ["x-opencode-session"] = "fixed-session"
        });

        var resolved = ProxySessionHeaderTemplate.Apply(route, "ses_abc");

        Assert.Same(route, resolved);
    }

    private static ProxyRouteDto Route(Dictionary<string, object?> headers)
    {
        return new ProxyRouteDto(
            new Dictionary<string, object?>
            {
                ["type"] = "chat",
                ["headers"] = headers
            },
            "model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: true);
    }

    private static ProxyRequestMetadata Request(IReadOnlyDictionary<string, string> headers)
    {
        return new ProxyRequestMetadata(
            "POST",
            "/v1/chat/completions",
            null,
            headers);
    }
}

using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProbeRequestInterceptorTests
{
    [Fact]
    public void TryIntercept_MessagesMaxTokensOne_ReturnsFakeMessagesResponse()
    {
        var payload = CreatePayload("claude-opus-5", "max_tokens", 1);

        var intercepted = ProbeRequestInterceptor.TryIntercept(
            ProtocolConverter.Messages,
            payload,
            "req-123",
            out var result);

        Assert.True(intercepted);
        Assert.NotNull(result);
        Assert.Equal(200, result.StatusCode);
        var response = Assert.IsType<Dictionary<string, object?>>(result.Payload);
        Assert.Equal("message", response["type"]);
        Assert.Equal("end_turn", response["stop_reason"]);
    }

    [Fact]
    public void TryIntercept_DoesNotInterceptWhenMaxTokensIsHigher()
    {
        var payload = CreatePayload("claude-opus-5", "max_tokens", 4096);

        var intercepted = ProbeRequestInterceptor.TryIntercept(
            ProtocolConverter.Messages,
            payload,
            "req-123",
            out var result);

        Assert.False(intercepted);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("max_output_tokens")]
    [InlineData("max_completion_tokens")]
    public void TryIntercept_SupportsProbeTokenKeys(string key)
    {
        var payload = CreatePayload("gpt-5.5", key, 1);

        var intercepted = ProbeRequestInterceptor.TryIntercept(
            ProtocolConverter.Responses,
            payload,
            "req-123",
            out var result);

        Assert.True(intercepted);
        Assert.NotNull(result);
        var response = Assert.IsType<Dictionary<string, object?>>(result.Payload);
        Assert.Equal("response", response["object"]);
    }

    [Fact]
    public void TryIntercept_ChatMaxTokensOne_ReturnsChatCompletionResponse()
    {
        var payload = CreatePayload("gpt-5.5", "max_tokens", 1);

        var intercepted = ProbeRequestInterceptor.TryIntercept(
            ProtocolConverter.Chat,
            payload,
            "req-123",
            out var result);

        Assert.True(intercepted);
        Assert.NotNull(result);
        var response = Assert.IsType<Dictionary<string, object?>>(result.Payload);
        Assert.Equal("chat.completion", response["object"]);
    }

    private static Dictionary<string, object?> CreatePayload(string model, string key, int maxTokens)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            [key] = maxTokens
        };
    }
}

using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyStreamServiceTests
{
    [Fact]
    public async Task CaptureStreamUsage_ForwardsAllLines()
    {
        var input = new[] { "event: message", "data: {}", "", "data: [DONE]" };

        var capture = new ProxyStreamService.PassThroughCapture();
        var result = new List<string>();
        await foreach (var line in ProxyStreamService.CaptureStreamUsage(
            ToAsyncEnumerable(input), capture, CancellationToken.None))
        {
            result.Add(line);
        }

        Assert.Equal(input, result);
    }

    [Fact]
    public async Task CaptureStreamUsage_ExtractsModelAndUsageFromChatSse()
    {
        var lines = new[]
        {
            "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o\",\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}",
            "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o\",\"choices\":[{\"delta\":{}}],\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":30,\"total_tokens\":80}}",
            "data: [DONE]"
        };

        var capture = new ProxyStreamService.PassThroughCapture();
        var result = new List<string>();
        await foreach (var line in ProxyStreamService.CaptureStreamUsage(
            ToAsyncEnumerable(lines), capture, CancellationToken.None))
        {
            result.Add(line);
        }

        Assert.NotNull(capture.UpstreamResponse);
        Assert.Equal("gpt-4o", capture.UpstreamResponse!["model"]);
        var usage = Assert.IsType<Dictionary<string, object?>>(capture.UpstreamResponse!["usage"]);
        Assert.Equal(50, Convert.ToInt32(usage["prompt_tokens"]));
        Assert.Equal(30, Convert.ToInt32(usage["completion_tokens"]));
    }

    [Fact]
    public async Task CaptureStreamUsage_ExtractsModelAndUsageFromResponsesSse()
    {
        var lines = new[]
        {
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp-1\",\"model\":\"gpt-4o\",\"status\":\"in_progress\"}}",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}",
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\",\"model\":\"gpt-4o\",\"status\":\"completed\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50,\"total_tokens\":150}}}",
            "data: [DONE]"
        };

        var capture = new ProxyStreamService.PassThroughCapture();
        var result = new List<string>();
        await foreach (var line in ProxyStreamService.CaptureStreamUsage(
            ToAsyncEnumerable(lines), capture, CancellationToken.None))
        {
            result.Add(line);
        }

        Assert.NotNull(capture.UpstreamResponse);
        Assert.Equal("gpt-4o", capture.UpstreamResponse!["model"]);
        var usage = Assert.IsType<Dictionary<string, object?>>(capture.UpstreamResponse!["usage"]);
        Assert.Equal(100, Convert.ToInt32(usage["input_tokens"]));
        Assert.Equal(50, Convert.ToInt32(usage["output_tokens"]));
    }

    [Fact]
    public async Task CaptureStreamUsage_ExtractsModelAndUsageFromMessagesSse()
    {
        var lines = new[]
        {
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-1\",\"model\":\"claude-sonnet\",\"type\":\"message\",\"role\":\"assistant\",\"usage\":{\"input_tokens\":200,\"output_tokens\":0}}}",
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":80}}",
            "data: [DONE]"
        };

        var capture = new ProxyStreamService.PassThroughCapture();
        var result = new List<string>();
        await foreach (var line in ProxyStreamService.CaptureStreamUsage(
            ToAsyncEnumerable(lines), capture, CancellationToken.None))
        {
            result.Add(line);
        }

        Assert.NotNull(capture.UpstreamResponse);
        Assert.Equal("claude-sonnet", capture.UpstreamResponse!["model"]);
        var usage = Assert.IsType<Dictionary<string, object?>>(capture.UpstreamResponse!["usage"]);
        Assert.Equal(200, Convert.ToInt32(usage["input_tokens"]));
    }

    [Fact]
    public async Task CaptureStreamUsage_NullWhenNoModelOrUsage()
    {
        var lines = new[]
        {
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}",
            "data: [DONE]"
        };

        var capture = new ProxyStreamService.PassThroughCapture();
        var result = new List<string>();
        await foreach (var line in ProxyStreamService.CaptureStreamUsage(
            ToAsyncEnumerable(lines), capture, CancellationToken.None))
        {
            result.Add(line);
        }

        Assert.Null(capture.UpstreamResponse);
    }

    [Fact]
    public async Task CaptureStreamUsage_HandlesNonJsonDataLines()
    {
        var lines = new[]
        {
            "data: not-json",
            "data: {\"model\":\"gpt-4o\",\"usage\":{\"input_tokens\":1,\"output_tokens\":2}}"
        };

        var capture = new ProxyStreamService.PassThroughCapture();
        var result = new List<string>();
        await foreach (var line in ProxyStreamService.CaptureStreamUsage(
            ToAsyncEnumerable(lines), capture, CancellationToken.None))
        {
            result.Add(line);
        }

        Assert.NotNull(capture.UpstreamResponse);
        Assert.Equal("gpt-4o", capture.UpstreamResponse!["model"]);
    }

    [Fact]
    public async Task CaptureStreamUsage_ExtractsTopLevelUsageOverNested()
    {
        // A SSE chunk that has usage at both top-level and inside nested object.
        // Top-level usage should be used (first-wins, the model key is also in both places).
        var lines = new[]
        {
            "data: {\"model\":\"top-model\",\"usage\":{\"input_tokens\":300},\"response\":{\"model\":\"nested-model\",\"usage\":{\"input_tokens\":999}}}"
        };

        var capture = new ProxyStreamService.PassThroughCapture();
        await foreach (var _ in ProxyStreamService.CaptureStreamUsage(
            ToAsyncEnumerable(lines), capture, CancellationToken.None))
        {
        }

        Assert.NotNull(capture.UpstreamResponse);
        // top-level model wins (first seen)
        Assert.Equal("top-model", capture.UpstreamResponse!["model"]);
        // top-level usage wins (first seen)
        var usage = Assert.IsType<Dictionary<string, object?>>(capture.UpstreamResponse!["usage"]);
        Assert.Equal(300, Convert.ToInt32(usage["input_tokens"]));
    }

    [Fact]
    public async Task CaptureStreamUsage_EmptyStreamYieldsNull()
    {
        var capture = new ProxyStreamService.PassThroughCapture();
        await foreach (var _ in ProxyStreamService.CaptureStreamUsage(
            ToAsyncEnumerable([]), capture, CancellationToken.None))
        {
        }

        Assert.Null(capture.UpstreamResponse);
    }

    [Fact]
    public async Task CaptureStreamUsage_ModelWithoutUsageProducesResponse()
    {
        // response.created provides model but no usage at all.
        var lines = new[]
        {
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"model\":\"gpt-mini\"}}",
            "data: [DONE]"
        };

        var capture = new ProxyStreamService.PassThroughCapture();
        await foreach (var _ in ProxyStreamService.CaptureStreamUsage(
            ToAsyncEnumerable(lines), capture, CancellationToken.None))
        {
        }

        Assert.NotNull(capture.UpstreamResponse);
        Assert.Equal("gpt-mini", capture.UpstreamResponse!["model"]);
        var usage = Assert.IsType<Dictionary<string, object?>>(capture.UpstreamResponse!["usage"]);
        Assert.Empty(usage);
    }

    private static async IAsyncEnumerable<string> ToAsyncEnumerable(
        IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }
}

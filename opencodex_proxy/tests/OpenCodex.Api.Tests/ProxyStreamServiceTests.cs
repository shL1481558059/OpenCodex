using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyStreamServiceTests
{
    [Fact]
    public async Task StreamAsync_MessagesWebSearchSimulation_UsesSimulatorBranch()
    {
        var upstream = new ThrowingUpstreamClient();
        var logs = new StubProxyLogService();
        var webSearch = new StubWebSearchSimulator(
            canSimulate: true,
            streamLines:
            [
                "event: response.created\ndata: {\"type\":\"response.created\"}\n\n",
                "event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n"
            ]);
        var service = new ProxyStreamService(upstream, logs, webSearch);
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "messages",
            ["type"] = ProtocolConverter.Messages
        };
        var route = new ProxyRouteDto(
            channel,
            "public-model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: true);
        var context = new ProxyStreamContext(
            startedTimestamp: 0,
            requestId: "req_1",
            ownerUsername: "admin",
            apiKeyId: 1,
            originalPayload: new Dictionary<string, object?>(),
            payload: new Dictionary<string, object?>
            {
                ["tools"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "web_search" } }
            },
            upstreamRequest: new Dictionary<string, object?>(),
            entryProtocol: ProtocolConverter.Responses,
            route: route,
            channelType: ProtocolConverter.Messages,
            channelId: "messages",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None);

        await service.StreamAsync(context);

        Assert.True(webSearch.StreamCalled);
        Assert.True(writer.Prepared);
        Assert.Equal(2, writer.Lines.Count);
        Assert.Contains("response.created", writer.Lines[0], StringComparison.Ordinal);
        Assert.Contains("response.completed", writer.Lines[1], StringComparison.Ordinal);
    }

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

    private sealed class CapturingProxyStreamWriter : IProxyStreamWriter
    {
        public bool Prepared { get; private set; }

        public List<string> Lines { get; } = [];

        public void PrepareSse()
        {
            Prepared = true;
        }

        public async Task<StreamWriteMetrics> WriteLinesAsync(
            IAsyncEnumerable<string> lines,
            Func<string, bool> countsForTtft,
            Func<int> elapsedMilliseconds,
            CancellationToken cancellationToken = default)
        {
            await foreach (var line in lines.WithCancellation(cancellationToken))
            {
                Lines.Add(line);
            }

            return new StreamWriteMetrics(ttftMs: 1);
        }
    }

    private sealed class ThrowingUpstreamClient : IUpstreamClient
    {
        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("should not use direct upstream post in simulator branch");
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new NotSupportedException("should not use direct upstream stream in simulator branch");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class StubWebSearchSimulator : IWebSearchSimulator
    {
        private readonly bool _canSimulate;
        private readonly IReadOnlyList<string> _streamLines;

        public StubWebSearchSimulator(bool canSimulate, IReadOnlyList<string> streamLines)
        {
            _canSimulate = canSimulate;
            _streamLines = streamLines;
        }

        public bool StreamCalled { get; private set; }

        public bool CanSimulate(
            string entryProtocol,
            string channelType,
            string ownerRole,
            IReadOnlyDictionary<string, object?> payload)
        {
            return _canSimulate;
        }

        public Task<WebSearchSimulationResult> RunAsync(
            IReadOnlyDictionary<string, object?> channel,
            Dictionary<string, object?> upstreamRequest,
            Dictionary<string, object?> payload,
            string? originalModel,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("non-stream path is not used in this test");
        }

        public async IAsyncEnumerable<string> RunChatStreamAsync(
            IReadOnlyDictionary<string, object?> channel,
            Dictionary<string, object?> upstreamRequest,
            Dictionary<string, object?> payload,
            string? originalModel,
            int defaultTimeout,
            WebSearchStreamResult result,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamCalled = true;
            result.ResponsePayload = new Dictionary<string, object?>();
            foreach (var line in _streamLines)
            {
                yield return line;
                await Task.Yield();
            }
        }
    }

    private sealed class StubProxyLogService : IProxyLogService
    {
        public long WriteLog(ProxyLogContext context, ProxyRequestMetadata request)
        {
            return 0;
        }

        public long WriteLog(ProxyRequestLogContext context)
        {
            return 0;
        }
    }
}

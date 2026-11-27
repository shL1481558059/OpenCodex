using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenCodex.Api.Hosting;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Errors;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Streaming;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class SseHttpEventPipelineTests
{
    [Fact]
    public void StartupSseOptions_UseRootParametersInsteadOfGeneralConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ProxyStreaming:MaxEventBytes"] = "1",
                ["ProxyStreaming:MaxCaptureBytes"] = "1",
                ["ProxyStreaming:MaxCaptureItems"] = "1",
                ["ProxyStreaming:MaxCaptureFields"] = "1",
                ["ProxyStreaming:MaxBufferedRoundBytes"] = "1",
                ["ProxyStreaming:MaxBufferedRoundFrames"] = "1",
                ["ProxyStreaming:MaxBatchBytes"] = "1",
                ["ProxyStreaming:MaxFlushDelayMilliseconds"] = "1",
                ["ProxyStreaming:MaxQueuedEvents"] = "1",
                ["ProxyStreaming:MaxQueuedBytes"] = "1",
                ["ProxyStreaming:Upstream:MaxEventBytes"] = "1",
                ["ProxyStreaming:Upstream:MaxProbeBytes"] = "1",
                ["ProxyStreaming:Upstream:MaxProbeFrames"] = "1",
                ["ProxyStreaming:Upstream:MaxWebSocketMessageBytes"] = "1"
            }).Build();
        var services = new ServiceCollection();
        services.AddOpenCodexApi(configuration);
        using var provider = services.BuildServiceProvider();
        var frames = provider.GetRequiredService<SseFrameOptions>();
        var transport = provider.GetRequiredService<SseTransportOptions>();
        var writer = provider.GetRequiredService<IOptions<SseWriteOptions>>().Value;
        var parameters = ProtocolConversionParameters.Current;
        Assert.Equal(parameters.SseMaxEventBytes, frames.MaxEventBytes);
        Assert.Equal(parameters.SseMaxCaptureBytes, frames.MaxCaptureBytes);
        Assert.Equal(parameters.SseMaxCaptureItems, frames.MaxCaptureItems);
        Assert.Equal(parameters.SseMaxCaptureFields, frames.MaxCaptureFields);
        Assert.Equal(parameters.SseMaxBufferedRoundBytes, frames.MaxBufferedRoundBytes);
        Assert.Equal(parameters.SseMaxBufferedRoundFrames, frames.MaxBufferedRoundFrames);
        Assert.Equal(parameters.SseMaxEventBytes, transport.MaxEventBytes);
        Assert.Equal(parameters.SseMaxProbeBytes, transport.MaxProbeBytes);
        Assert.Equal(parameters.SseMaxProbeFrames, transport.MaxProbeFrames);
        Assert.Equal(parameters.SseMaxWebSocketMessageBytes, transport.MaxWebSocketMessageBytes);
        Assert.Equal(parameters.SseMaxEventBytes, writer.MaxEventBytes);
        Assert.Equal(parameters.SseMaxBatchBytes, writer.MaxBatchBytes);
        Assert.Equal(parameters.SseMaxFlushDelayMilliseconds, writer.MaxFlushDelayMilliseconds);
        Assert.Equal(parameters.SseMaxQueuedEvents, writer.MaxQueuedEvents);
        Assert.Equal(parameters.SseMaxQueuedBytes, writer.MaxQueuedBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(4096)]
    public void SpanInput_IsConsumedBeforeBufferReuseAndMatchesLegacyFrames(int chunkCharacters)
    {
        const string wire = "\uFEFF: heartbeat\r\n\r\nevent: delta\r\ndata: {\r\ndata:  \"delta\":\"汉😀\"}\r\n\r\ndata: [DONE]\r\n\r\ndata: {bad}";
        var expectedParser = new SseFrameReader();
        var expected = expectedParser.Feed(wire).ToList();
        expected.Add(expectedParser.Complete()!);
        var parser = new SseFrameReader();
        var frames = new Queue<SseFrame>();
        var buffer = new char[chunkCharacters];
        for (var offset = 0; offset < wire.Length; offset += chunkCharacters)
        {
            var count = Math.Min(chunkCharacters, wire.Length - offset);
            wire.AsSpan(offset, count).CopyTo(buffer);
            parser.Feed(buffer.AsSpan(0, count), frames);
            buffer.AsSpan().Fill('!');
        }
        frames.Enqueue(parser.Complete()!);
        Assert.Equal(expected.Select(frame => frame.WireText), frames.Select(frame => frame.WireText));
        Assert.Equal(expected.Select(frame => frame.BoundaryComplete), frames.Select(frame => frame.BoundaryComplete));
        Assert.Equal(expected.Select(frame => frame.Malformed), frames.Select(frame => frame.Malformed));
        Assert.Equal(expected.Select(frame => System.Text.Json.JsonSerializer.Serialize(frame.Event)),
            frames.Select(frame => System.Text.Json.JsonSerializer.Serialize(frame.Event)));
    }

    [Fact]
    public async Task HttpProbe_MultilineErrorRetries_ThenSharesDecodedPartialToolArguments()
    {
        var handler = new Handler(
            ": heartbeat\n\ndata:\n\nevent: error\ndata: {\ndata:  \"type\":\"error\",\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"limited\"}}\n\n",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"a__b\",\"arguments\":\"{\\\"x\\\":\"}}]}}]}\n\ndata: [DONE]\n\n");
        var client = new HttpUpstreamClient(new HttpClient(handler), (_, _) => Task.CompletedTask);
        var frames = new List<SseFrame>();
        await foreach (var frame in client.StreamEventsAsync(Channel(), new Dictionary<string, object?>(), 10, default)) frames.Add(frame);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, frames.Count);
        Assert.IsType<Dictionary<string, object?>>(frames[0].Event!.Data);
        Assert.False(frames[0].Malformed);
        Assert.True(frames[1].IsDone);
        Assert.Contains("a__b", frames[0].WireText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpHugePhysicalLine_ExplicitlyFailsConfiguredEventLimit()
    {
        var handler = new Handler("data: \"" + new string('x', 100_000) + "\"\n\n");
        var client = new HttpUpstreamClient(new HttpClient(handler), streamOptions: new() { MaxEventBytes = 1024 });
        var error = await Assert.ThrowsAsync<UpstreamException>(async () =>
        {
            await foreach (var _ in client.StreamEventsAsync(Channel(), new Dictionary<string, object?>(), 10, default)) { }
        });
        Assert.Equal(502, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task HttpSkeletonProbe_HasBoundedFrameBudgetAndDoesNotSilentlyDiscardAudit()
    {
        var skeleton = "data: {\"type\":\"response.created\"}\n\n";
        var handler = new Handler(string.Concat(Enumerable.Repeat(skeleton, 10)));
        var client = new HttpUpstreamClient(new HttpClient(handler), streamOptions: new() { MaxProbeFrames = 3 });
        var error = await Assert.ThrowsAsync<UpstreamException>(async () =>
        {
            await foreach (var _ in client.StreamEventsAsync(Channel(), new Dictionary<string, object?>(), 10, default)) { }
        });
        Assert.Contains("buffer limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    private static Dictionary<string, object?> Channel() => new()
    {
        ["type"] = "chat", ["baseurl"] = "https://offline.invalid/v1", ["apikey"] = "offline-fixture", ["retry_count"] = 1
    };
    private sealed class Handler(params string[] bodies) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = bodies[Calls++];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
        }
    }
}

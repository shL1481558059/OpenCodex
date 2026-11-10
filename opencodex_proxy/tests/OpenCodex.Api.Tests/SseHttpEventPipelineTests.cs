using System.Net;
using System.Text;
using OpenCodex.Core.Errors;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class SseHttpEventPipelineTests
{
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

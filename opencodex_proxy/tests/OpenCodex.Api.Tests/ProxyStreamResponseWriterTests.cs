using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyStreamResponseWriterTests
{
    [Fact]
    public async Task WriteLinesAsync_RecordsGranularStreamMetrics()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var ticks = new Queue<int>(new[] { 10, 20, 30, 40 });
        var metrics = await ProxyStreamResponseWriter.WriteLinesAsync(
            context.Response,
            ToAsyncEnumerable(
                "event: response.created\ndata: {\"type\":\"response.created\"}\n\n",
                "event: response.reasoning_summary_text.delta\ndata: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"think\"}\n\n",
                "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}\n\n",
                "event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n"),
            SseStreamConverter.CountsForTtft,
            () => ticks.Dequeue(),
            CancellationToken.None);

        Assert.Equal(10, metrics.FirstSseEventMs);
        Assert.Equal(20, metrics.TtftMs);
        Assert.Equal(20, metrics.FirstReasoningSummaryTextDeltaMs);
        Assert.Equal(30, metrics.FirstOutputTextDeltaMs);
        Assert.Equal(40, metrics.CompletedEventMs);
    }

    private static async IAsyncEnumerable<string> ToAsyncEnumerable(params string[] lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }
}

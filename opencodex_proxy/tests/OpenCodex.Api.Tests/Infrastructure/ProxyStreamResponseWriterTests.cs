using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Infrastructure;

public sealed class ProxyStreamResponseWriterTests
{
    [Fact]
    public void PrepareSseSetsStreamingHeaders()
    {
        var context = new DefaultHttpContext();

        ProxyStreamResponseWriter.PrepareSse(context.Response);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl);
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"]);
    }

    [Fact]
    public async Task WriteLinesAsyncWritesAllLinesAndCalculatesFirstMatchingTtft()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var elapsedCalls = 0;

        var ttftMs = await ProxyStreamResponseWriter.WriteLinesAsync(
            context.Response,
            Lines(["\n", "data: first\n", "data: second\n"]),
            static line => line.StartsWith("data:", StringComparison.Ordinal),
            () =>
            {
                elapsedCalls++;
                return 42;
            });

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();

        Assert.Equal(42, ttftMs);
        Assert.Equal(1, elapsedCalls);
        Assert.Equal("\ndata: first\ndata: second\n", body);
    }

    [Fact]
    public async Task WriteLinesAsyncReturnsNullWhenNoLineCountsForTtft()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var ttftMs = await ProxyStreamResponseWriter.WriteLinesAsync(
            context.Response,
            Lines(["\n", "\n"]),
            static _ => false,
            static () => throw new InvalidOperationException("TTFT should not be measured"));

        Assert.Null(ttftMs);
    }

    private static async IAsyncEnumerable<string> Lines(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }
}

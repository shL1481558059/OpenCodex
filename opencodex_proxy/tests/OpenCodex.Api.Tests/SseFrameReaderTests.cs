using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class SseFrameReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task ArbitraryChunks_MultilineCrLfCommentsUnicodeAndFieldSpaces(int size)
    {
        const string wire = ": heartbeat\r\n\r\nid: 7\r\nevent: response.output_text.delta\r\ndata: {\r\ndata:  \"type\":\"response.output_text.delta\",\"delta\":\"汉😀\"}\r\n\r\n";
        var frames = new List<SseFrame>();
        await foreach (var frame in SseFrameReader.ReadChunks(Chunks(wire, size))) frames.Add(frame);
        Assert.Equal(2, frames.Count);
        Assert.Null(frames[0].Event);
        var payload = Assert.IsType<Dictionary<string, object?>>(frames[1].Event!.Data);
        Assert.Equal("汉😀", payload["delta"]);
        Assert.Equal("response.output_text.delta", frames[1].Type);
        Assert.True(frames[1].BoundaryComplete);
        Assert.False(frames[1].Malformed);
        Assert.Equal(wire.Replace("\r\n", "\n"), string.Concat(frames.Select(f => f.WireText)));
    }

    [Fact]
    public async Task LegacyLinesAndFullWsFrames_ProduceSameEventsAndOwnedData()
    {
        const string json = "{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":12}}}";
        var full = new SseFrameReader().Feed("event: response.completed\ndata: " + json + "\n\n").Single();
        var frames = new List<SseFrame>();
        await foreach (var frame in SseFrameReader.ReadLines(Lines("event: response.completed", "data: " + json, ""))) frames.Add(frame);
        Assert.Equal(full.WireText, frames.Single().WireText);
        var original = JsonSerializer.Serialize(full.Event!.Data);
        var capture = new StreamResponseCapture(ProtocolConverter.Responses);
        capture.Accept(full);
        Assert.True(capture.Complete(StreamCaptureTermination.Completed).Completed);
        var result = new ConvertedStreamResult();
        await foreach (var _ in SseStreamConverter.ResponsesToChatEvents(Decoded(full.Event), "m", result, null, CancellationToken.None)) { }
        Assert.NotNull(result.UpstreamResponse);
        Assert.Equal(original, JsonSerializer.Serialize(full.Event.Data));
        Assert.True(full.IsSuccessfulResponseCompleted);
    }

    [Fact]
    public void IncompleteOrMalformedFrames_NeverClaimSuccessfulTerminalFlush()
    {
        var parser = new SseFrameReader();
        Assert.Empty(parser.Feed("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n"));
        Assert.False(parser.Complete()!.IsSuccessfulResponseCompleted);
        var malformed = new SseFrameReader().Feed("data: {bad}\n\n").Single();
        Assert.True(malformed.Malformed);
        Assert.False(malformed.IsTerminal);
        Assert.False(new SseFrameReader().Feed("data: {\"delta\":\"response.completed\"}\n\n").Single().IsTerminal);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("汉")]
    public void EventLimit_ExplicitFailure_NotSilentTruncation(string text)
    {
        var parser = new SseFrameReader(new() { MaxEventBytes = 80 });
        Assert.Throws<InvalidDataException>(() => parser.Feed("data: \"" + string.Concat(Enumerable.Repeat(text, 100)) + "\"\n\n").ToList());
    }

    [Fact]
    public async Task EventOverOldCapturePendingLimit_IsStillParsedAndDelivered()
    {
        var text = new string('x', 300_000);
        var wire = "data: " + JsonSerializer.Serialize(new { delta = text }) + "\n\n";
        var frames = new List<SseFrame>();
        await foreach (var frame in SseFrameReader.ReadChunks(Chunks(wire, 4096))) frames.Add(frame);
        Assert.Equal(text, Assert.IsType<Dictionary<string, object?>>(frames.Single().Event!.Data)["delta"]);
    }

    private static async IAsyncEnumerable<string> Chunks(string wire, int size)
    {
        for (var i = 0; i < wire.Length; i += size) { yield return wire.Substring(i, Math.Min(size, wire.Length - i)); await Task.Yield(); }
    }
    private static async IAsyncEnumerable<string> Lines(params string[] lines)
    {
        foreach (var line in lines) { yield return line; await Task.Yield(); }
    }
    private static async IAsyncEnumerable<SseEvent> Decoded(SseEvent value) { yield return value; await Task.CompletedTask; }
}

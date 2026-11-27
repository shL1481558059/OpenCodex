using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Infrastructure;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Streaming;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class SseFrameReaderTests
{
    [Fact]
    public void CompleteLfEventReusesItsOwnedInputString()
    {
        var wire = "data: " + JsonSerializer.Serialize(new { text = "中😀" }) + "\n\n";
        var frame = Assert.Single(new SseFrameReader().Feed(wire));
        Assert.Same(wire, frame.WireText);
    }

    [Fact]
    public void OwnedJsonArrayUsesExactCapacityAndPreservesNumericTypes()
    {
        var data = new SseFrameReader().Feed(
            "data: {\"values\":[0,1,2,3,4,5,6,7,8,9,10,11,12,13,2147483648,1.25,null]}\n\n").Single().Event!.Data;
        var values = Assert.IsType<List<object?>>(Assert.IsType<Dictionary<string, object?>>(data)["values"]);
        Assert.Equal(values.Count, values.Capacity);
        Assert.IsType<int>(values[0]);
        Assert.IsType<long>(values[14]);
        Assert.IsType<double>(values[15]);
        Assert.Null(values[16]);
    }

    [Fact]
    public void DuplicateJsonPropertiesRetainTheirExplicitFailure()
    {
        Assert.Throws<ArgumentException>(() =>
            new SseFrameReader().Feed("data: {\"key\":1,\"key\":2}\n\n").ToList());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void EveryBoundarySplitPreservesWireAndEventsForBothFeedApis(string newline)
    {
        var wire = "\uFEFFdata: {\"text\":\"中😀\"}" + newline + newline
            + ": comment" + newline + "data: [DONE]" + newline + newline + "data: null";
        var expected = wire.Replace("\r\n", "\n").Replace('\r', '\n');
        for (var split = 0; split <= wire.Length; split++)
        {
            var parser = new SseFrameReader();
            var frames = parser.Feed(wire[..split]).Concat(parser.Feed(wire[split..])).ToList();
            frames.Add(parser.Complete()!);
            var spanParser = new SseFrameReader();
            var queue = new Queue<SseFrame>();
            spanParser.Feed(wire.AsSpan(0, split), queue);
            spanParser.Feed(wire.AsSpan(split), queue);
            queue.Enqueue(spanParser.Complete()!);
            Assert.Equal(expected, string.Concat(frames.Select(frame => frame.WireText)));
            Assert.Equal(frames.Select(frame => frame.WireText), queue.Select(frame => frame.WireText));
            Assert.Equal("中😀", Assert.IsType<Dictionary<string, object?>>(frames[0].Event!.Data)["text"]);
            Assert.True(frames[1].IsDone);
            Assert.False(frames[2].BoundaryComplete);
            Assert.False(frames[2].Malformed);
        }
    }
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

    [Theory]
    [InlineData("data\n\n", "", true)]
    [InlineData("data:\n\n", "", true)]
    [InlineData("data: \n\n", "", true)]
    [InlineData("data:\ndata:\n\n", "\n", true)]
    [InlineData("data:  [DONE]\n\n", " [DONE]", true)]
    [InlineData("data: [DONE]\n\n", "[DONE]", false)]
    [InlineData("data: {bad}\ndata: tail\n\n", "{bad}\ntail", true)]
    public void EmptyAndMultilineData_PreservePayloadAndMalformedState(string wire, string data, bool malformed)
    {
        var frame = new SseFrameReader().Feed(wire).Single();
        Assert.Equal(wire, frame.WireText);
        Assert.Equal(data, frame.Event!.Data);
        Assert.Equal(malformed, frame.Malformed);
        Assert.Equal(data == "[DONE]", frame.IsDone);
    }

    [Fact]
    public void FieldsAfterDataAndUnknownFields_PreserveWireAndLastEventName()
    {
        const string wire = "\uFEFFid: 7\n: comment\ndata: {\"delta\":\"first\"}\nevent: ignored\nretry: 5\nevent: final\n\n";
        var frame = new SseFrameReader().Feed(wire).Single();
        Assert.Equal(wire, frame.WireText);
        Assert.Equal("final", frame.Event!.EventName);
        Assert.Equal("first", Assert.IsType<Dictionary<string, object?>>(frame.Event.Data)["delta"]);
    }

    [Fact]
    public void BomIsOnlyStrippedFromFirstNonemptyLine()
    {
        var parser = new SseFrameReader();
        var frames = parser.Feed("\n\uFEFFdata: null\n\n\uFEFFdata: [DONE]\n\n").ToList();
        Assert.Equal(3, frames.Count);
        Assert.Null(frames[0].Event);
        Assert.NotNull(frames[1].Event);
        Assert.Null(frames[1].Event!.Data);
        Assert.Null(frames[2].Event);
    }

    [Fact]
    public void MultipleDataLines_JoinOnceAndRespectEmptyEventName()
    {
        const string wire = "event: first\ndata: {\nevent:\ndata:  \"values\": [1,\ndata:  2, 3]}\n\n";
        var frame = new SseFrameReader().Feed(wire).Single();
        Assert.Equal(wire, frame.WireText);
        Assert.Equal("", frame.Event!.EventName);
        Assert.False(frame.Malformed);
        var payload = Assert.IsType<Dictionary<string, object?>>(frame.Event.Data);
        Assert.Equal(new object?[] { 1, 2, 3 }, Assert.IsType<List<object?>>(payload["values"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("汉😀")]
    public void ExactUtf8EventLimit_IsIndependentOfChunkBoundaries(string text)
    {
        var wire = "data: \"" + text + "\"\r\n\r\n";
        var normalized = wire.Replace("\r\n", "\n");
        var bytes = Encoding.UTF8.GetByteCount(normalized);
        var parser = new SseFrameReader(new() { MaxEventBytes = bytes });
        var frames = new List<SseFrame>();
        foreach (var character in wire) frames.AddRange(parser.Feed(character.ToString()));
        Assert.Equal(normalized, Assert.Single(frames).WireText);
        var tooSmall = new SseFrameReader(new() { MaxEventBytes = bytes - 1 });
        Assert.Throws<InvalidDataException>(() => tooSmall.Feed(wire).ToList());
    }

    [Fact]
    public void DeliveredFrame_RemainsOwnedAfterParserReuseAndCompletion()
    {
        var parser = new SseFrameReader();
        const string firstWire = "data: {\"nested\":{\"text\":\"汉😀\"}}\n\n";
        var first = parser.Feed(firstWire).Single();
        var original = JsonSerializer.Serialize(first.Event!.Data);
        Assert.True(parser.Feed("data: [DONE]\n\n").Single().IsDone);
        Assert.Empty(parser.Feed("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}"));
        var incomplete = parser.Complete()!;
        Assert.False(incomplete.BoundaryComplete);
        Assert.False(incomplete.IsSuccessfulResponseCompleted);
        Assert.Null(parser.Complete());
        Assert.Equal(firstWire, first.WireText);
        Assert.Equal(original, JsonSerializer.Serialize(first.Event.Data));
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

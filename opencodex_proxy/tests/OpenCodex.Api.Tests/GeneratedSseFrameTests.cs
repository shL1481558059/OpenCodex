using System.Text.Json;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Domain.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class GeneratedSseFrameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("response.completed")]
    public void CountingLargeEscapedUnicodeFrame_DoesNotMaterializeWireAndRetainsExactLegacyBytes(string? name)
    {
        var value = string.Concat(Enumerable.Repeat("汉😀\"\n\\\t", 20_000));
        var data = new Dictionary<string,object?> { ["type"]="response.completed", ["value"]=value, ["int"]=42, ["nil"]=null };
        var frame = SseFrame.FromJson(data,name);
        var json = JsonSerializer.Serialize(data,new JsonSerializerOptions { Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var expected = name is null ? $"data: {json}\n\n" : $"event: {name}\ndata: {json}\n\n";
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(expected),frame.Utf8Length);
        Assert.False(frame.IsWireMaterialized);
        Assert.Equal(expected,frame.WireText);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(expected),frame.Utf8Length);
    }

    [Fact]
    public void DiagnosticCapture_DoesNotMaterializeLargeGeneratedTerminal()
    {
        var frame = SseFrame.FromJson(new Dictionary<string,object?> { ["type"]="response.completed", ["value"]=new string('x',1024*1024) },"response.completed");
        var capture = new OpenCodex.Core.Services.Proxy.StreamLogCapture("responses");
        capture.Observe(frame);
        Assert.True(capture.SawTerminalEvent);
        Assert.Equal(2000,capture.LastDataLine!.Length);
        Assert.False(frame.IsWireMaterialized);
        var wire = frame.WireText;
        var expected = wire[wire.IndexOf("data:",StringComparison.Ordinal)..];
        Assert.Equal(expected[..2000],capture.LastDataLine);
    }

    [Fact]
    public async Task GeneratedFrame_FreezesNestedAndBorrowedValues_EncodesOnceLazily()
    {
        var child = new Dictionary<string, object?> { ["status"] = "in_progress" };
        var list = new List<object?> { child };
        SseFrame frame;
        using (var doc = JsonDocument.Parse("{\"value\":1}"))
        {
            frame = SseFrame.FromJson(new Dictionary<string, object?>
            {
                ["type"] = "response.output_item.added", ["items"] = list,
                ["borrowed"] = doc.RootElement, ["typed"] = new Dictionary<string,string> { ["key"] = "value" },
                ["numbers"] = new[] { 1, 2, 3 }
            }, "response.output_item.added");
        }
        Assert.False(frame.IsWireMaterialized);
        child["status"] = "completed";
        list.Clear();
        var root = Assert.IsType<Dictionary<string, object?>>(frame.Event!.Data);
        Assert.Equal("in_progress", Assert.IsType<Dictionary<string,object?>>(Assert.IsType<List<object?>>(root["items"]).Single())["status"]);
        Assert.Equal("value", Assert.IsType<Dictionary<string,object?>>(root["typed"])["key"]);
        var wires = await Task.WhenAll(Enumerable.Range(0,16).Select(_ => Task.Run(() => frame.WireText)));
        Assert.All(wires, wire => Assert.Same(wires[0], wire));
        Assert.True(frame.IsWireMaterialized);
        AssertMatchesWire(frame);
    }

    [Fact]
    public void GeneratedFrame_RejectsCyclesInsteadOfRetainingLiveProducerGraph()
    {
        var cycle = new Dictionary<string, object?>(); cycle["self"] = cycle;
        Assert.Throws<JsonException>(() => SseFrame.FromJson(cycle));
    }

    [Theory]
    [InlineData("chat", "responses")]
    [InlineData("chat", "messages")]
    [InlineData("messages", "responses")]
    [InlineData("messages", "chat")]
    [InlineData("responses", "chat")]
    [InlineData("responses", "messages")]
    public async Task AllNativeConverters_ProduceFrozenTypedFramesWithEquivalentWireAndUsage(string from, string to)
    {
        var result = new ConvertedStreamResult();
        var frames = new List<SseFrame>();
        var source = Input(from);
        var output = (from,to) switch
        {
            ("chat","responses") => SseStreamConverter.ChatToResponsesFrames(source,"visible",result,null,false,0,0,default),
            ("chat","messages") => SseStreamConverter.ChatToMessagesFrames(source,"visible",result,null,false,default),
            ("messages","responses") => SseStreamConverter.MessagesToResponsesFrames(source,"visible",result,null,false,0,0,default),
            ("messages","chat") => SseStreamConverter.MessagesToChatFrames(source,"visible",result,null,true,default),
            ("responses","chat") => SseStreamConverter.ResponsesToChatFrames(source,"visible",result,null,default),
            _ => SseStreamConverter.ResponsesToMessagesFrames(source,"visible",result,null,false,default)
        };
        await foreach (var frame in output) { if (!frame.IsDone) Assert.False(frame.IsWireMaterialized); frames.Add(frame); }
        Assert.NotNull(result.UpstreamResponse);
        Assert.NotEmpty(frames);
        Assert.All(frames, AssertMatchesWire);
        Assert.Single(frames.Where(f => f.IsTerminal));
        Assert.Contains(frames, f => f.WireText.Contains("hello",StringComparison.Ordinal));
        var capture = new StreamResponseCapture(to);
        foreach(var frame in frames) capture.Accept(frame);
        var captured = capture.Complete(StreamCaptureTermination.Completed);
        Assert.True(captured.Completed);
        Assert.NotNull(captured.Response);
        Assert.True(captured.Response.ContainsKey("usage"));
    }

    [Fact]
    public void SearchTypedEvents_KeepAddedSnapshotAndDoNotMutateObservedInput()
    {
        var state = new WebSearchStreamEventState();
        var legacy = new WebSearchStreamEventState();
        var created = SseFrame.FromJson(new Dictionary<string, object?> { ["type"]="response.created", ["response"] = new Dictionary<string,object?> { ["id"]="r1",["created_at"]=1 } },"response.created");
        Assert.Equal(legacy.Observe(created.WireText,[]), state.Observe(created,[]).WireText);
        var delta = SseFrame.FromJson(new Dictionary<string,object?> { ["response_id"]="r2",["delta"]="hello" },"response.output_text.delta");
        var observed = state.Observe(delta,[]);
        Assert.Equal(legacy.Observe(delta.WireText,[]),observed.WireText);
        Assert.Equal("r2",Assert.IsType<Dictionary<string,object?>>(delta.Event!.Data)["response_id"]);
        var added = state.EmitWebSearchAddedFrame("s1","query",out var index);
        var expectedAdded = legacy.EmitWebSearchAdded("s1","query",out _);
        state.EmitWebSearchInProgressFrame("s1");legacy.EmitWebSearchInProgress("s1");
        state.EmitWebSearchCompletedFrame("s1");legacy.EmitWebSearchCompleted("s1");
        Assert.False(added.IsWireMaterialized);
        Assert.Equal(expectedAdded,added.WireText);
        Assert.Equal("in_progress",Assert.IsType<Dictionary<string,object?>>(Assert.IsType<Dictionary<string,object?>>(added.Event!.Data)["item"])["status"]);
        var result = WebSearchToolResult.Failed("s1","query","offline injected");
        Assert.Equal(legacy.EmitWebSearchDone(index,result),state.EmitWebSearchDoneFrame(index,result).WireText);
        var terminal = SseFrame.FromJson(new Dictionary<string,object?> { ["response"] = new Dictionary<string,object?> { ["id"]="other",["status"]="completed" } },"response.completed");
        Assert.True(WebSearchStreamEventState.IsTerminal(terminal));
        var actual = state.Complete(terminal,new() { ["input_tokens"]=3 },null,out var final);
        var expected = legacy.Complete(terminal.WireText,new() { ["input_tokens"]=3 },null,out var expectedFinal);
        Assert.Equal(expected,actual.WireText);
        Assert.Equal(JsonSerializer.Serialize(expectedFinal),JsonSerializer.Serialize(final));
        AssertMatchesWire(actual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BufferedRoundBudget_FailsWithoutTruncatingOrLeavingSourceActive(bool bytes)
    {
        var first = SseFrame.FromJson(new Dictionary<string, object?> { ["delta"] = "x" });
        var disposed = false;
        var read = 0;
        var options = bytes ? new SseFrameOptions { MaxBufferedRoundBytes = first.Utf8Length }
            : new SseFrameOptions { MaxBufferedRoundFrames = 1 };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            OpenCodex.Core.Services.Proxy.ProxyStreamService.CollectFramesAsync(Source(),options,default));
        Assert.Contains("round",error.Message,StringComparison.Ordinal);
        Assert.True(disposed);
        Assert.Equal(2,read);
        async IAsyncEnumerable<SseFrame> Source()
        {
            try { for(var i=0;i<10;i++) { read++;yield return first;await Task.CompletedTask; } }
            finally { disposed=true; }
        }
    }

    [Fact]
    public async Task BufferedRound_AtExactByteBoundaryRemainsOrderedAndDequeueReleasesItems()
    {
        var frame = SseFrame.FromJson(new Dictionary<string,object?> { ["delta"]="汉😀" });
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(frame.WireText),frame.Utf8Length);
        var queue = await OpenCodex.Core.Services.Proxy.ProxyStreamService.CollectFramesAsync(Source(),
            new SseFrameOptions { MaxBufferedRoundBytes=frame.Utf8Length+SseFrame.Done.Utf8Length, MaxBufferedRoundFrames=2 },default);
        Assert.Equal(2,queue.Count);
        Assert.Same(frame,queue.Dequeue());Assert.Same(SseFrame.Done,queue.Dequeue());Assert.Empty(queue);
        async IAsyncEnumerable<SseFrame> Source() { yield return frame;yield return SseFrame.Done;await Task.CompletedTask; }
    }

    private static void AssertMatchesWire(SseFrame frame)
    {
        if(frame.IsDone) { Assert.Equal("data: [DONE]\n\n",frame.WireText);return; }
        var decoded = new SseFrameReader().Feed(frame.WireText).Single();
        Assert.Equal(frame.Type,decoded.Type);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(frame.Event!.Data),JsonSerializer.SerializeToElement(decoded.Event!.Data)));
    }
    private static async IAsyncEnumerable<SseEvent> Input(string protocol)
    {
        string[] jsons = protocol switch
        {
            "chat" => [
                "{\"id\":\"c1\",\"model\":\"upstream\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"hello\"},\"finish_reason\":null}]}",
                "{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}",
                "{\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}", "[DONE]"],
            "messages" => [
                "{\"type\":\"message_start\",\"message\":{\"id\":\"m1\",\"model\":\"upstream\",\"role\":\"assistant\",\"content\":[],\"usage\":{\"input_tokens\":3}}}",
                "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}",
                "{\"type\":\"content_block_stop\",\"index\":0}",
                "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}", "{\"type\":\"message_stop\"}"],
            _ => [
                "{\"type\":\"response.created\",\"response\":{\"id\":\"r1\",\"model\":\"upstream\",\"status\":\"in_progress\",\"output\":[]}}",
                "{\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"hello\"}",
                "{\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"model\":\"upstream\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"id\":\"msg1\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"hello\",\"annotations\":[]}]}],\"usage\":{\"input_tokens\":3,\"output_tokens\":2,\"total_tokens\":5}}}"]
        };
        foreach(var json in jsons)
        {
            var parsed = new SseFrameReader().Feed("data: "+json+"\n\n").Single().Event!;
            yield return parsed;
            await Task.CompletedTask;
        }
    }
}

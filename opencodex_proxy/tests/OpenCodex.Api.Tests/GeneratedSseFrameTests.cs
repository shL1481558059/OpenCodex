using System.Text.Json;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Models;
using OpenCodex.ProtocolConversion.Streaming;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Domain.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class GeneratedSseFrameTests
{
    [Fact]
    public async Task ChatToMessagesKeepsYieldedSnapshots()
    {
        var result = new ConvertedStreamResult();
        await AssertRetainedSnapshots(SseStreamConverter.ChatToMessagesFrames(
            Input("chat"), "visible", result, null, false, default), result);
    }

    [Fact]
    public async Task ResponsesToMessagesKeepsYieldedSnapshots()
    {
        var result = new ConvertedStreamResult();
        await AssertRetainedSnapshots(SseStreamConverter.ResponsesToMessagesFrames(
            Input("responses"), "visible", result, null, false, default), result);
    }

    [Fact]
    public async Task MessagesToChatKeepsYieldedSnapshots()
    {
        var result = new ConvertedStreamResult();
        await AssertRetainedSnapshots(SseStreamConverter.MessagesToChatFrames(
            Input("messages"), "visible", result, null, true, default), result);
    }

    private static async Task AssertRetainedSnapshots(IAsyncEnumerable<SseFrame> source, ConvertedStreamResult result)
    {
        var snapshots = new List<(SseFrame Frame, string Json)>();
        await foreach (var frame in source)
            snapshots.Add((frame, JsonSerializer.Serialize(frame.Event!.Data)));
        result.UpstreamResponse!["model"] = "changed";
        Assert.Contains(snapshots, item => item.Json.Contains("hello", StringComparison.Ordinal));
        foreach (var (frame, json) in snapshots)
            Assert.Equal(json, JsonSerializer.Serialize(frame.Event!.Data));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("event data: 😀")]
    public void SmallDiagnosticPrefixesPreserveUtf16SlicesWithoutMaterializingWire(string? name)
    {
        for (var limit = 1; limit <= 64; limit++)
        {
            var frame = SseFrame.FromJson(new Dictionary<string, object?>
            {
                ["text"] = "😀中\"\ncontent"
            }, name);
            var prefix = frame.DiagnosticDataPrefix(limit);
            Assert.False(frame.IsWireMaterialized);
            var wire = frame.WireText.AsSpan().Trim().ToString();
            var start = wire.IndexOf("data:", StringComparison.Ordinal);
            Assert.Equal(wire.Substring(start, Math.Min(wire.Length - start, limit)), prefix);
        }
    }

    [Theory]
    [InlineData("response.output_item.done")]
    [InlineData("response.function_call_arguments.delta")]
    [InlineData("ordinary")]
    public void TypedTtftPreservesNestedKeyAndValueSubstringMatching(string marker)
    {
        var frame = SseFrame.FromJson(new Dictionary<string, object?>
        {
            ["nested"] = new List<object?>
            {
                new Dictionary<string, object?> { ["prefix" + marker + "suffix"] = "value" },
                new Dictionary<string, object?> { ["text"] = "prefix" + marker + "suffix" }
            }
        });
        var typed = SseFramePredicate.Evaluate(SseStreamConverter.FrameCountsForTtft, frame);
        Assert.False(frame.IsWireMaterialized);
        Assert.Equal(SseStreamConverter.CountsForTtft(frame.WireText), typed);
    }
    [Fact]
    public async Task ResponsesToChatPreservesBorrowedAnnotationSnapshot()
    {
        var annotation = new Dictionary<string, object?> { ["type"] = "url_citation", ["url"] = "before" };
        var observed = new List<(SseFrame Frame, string Snapshot)>();
        await foreach (var frame in SseStreamConverter.ResponsesToChatFrames(
            Source(), "visible", new ConvertedStreamResult(), null, default))
            observed.Add((frame, JsonSerializer.Serialize(frame.Event?.Data)));
        Assert.Equal("after", annotation["url"]);
        Assert.Contains(observed, item => item.Snapshot.Contains("before", StringComparison.Ordinal));
        foreach (var (frame, snapshot) in observed)
        {
            Assert.Equal(snapshot, JsonSerializer.Serialize(frame.Event?.Data));
            AssertMatchesWire(frame);
        }

        async IAsyncEnumerable<SseEvent> Source()
        {
            yield return new SseEvent("response.output_text.annotation.added", new Dictionary<string, object?>
            {
                ["type"] = "response.output_text.annotation.added", ["annotation"] = annotation
            });
            annotation["url"] = "after";
            yield return new SseEvent("response.output_text.delta", new Dictionary<string, object?>
            {
                ["type"] = "response.output_text.delta", ["delta"] = "text"
            });
            yield return new SseEvent("response.completed", new Dictionary<string, object?>
            {
                ["type"] = "response.completed",
                ["response"] = new Dictionary<string, object?> { ["status"] = "completed", ["output"] = new List<object?>() }
            });
            await Task.CompletedTask;
        }
    }
    [Fact]
    public void CountingLargeFrame_UsesBoundedEncodingMemory()
    {
        _ = SseFrame.FromJson(new Dictionary<string, object?> { ["text"] = new string('x', 64 * 1024) }).Utf8Length;
        var text = new string('x', 8 * 1024 * 1024);
        var frame = SseFrame.FromJson(new Dictionary<string, object?> { ["text"] = text });

        var before = GC.GetAllocatedBytesForCurrentThread();
        var length = frame.Utf8Length;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(length > text.Length);
        Assert.True(allocated < text.Length, $"Counting allocated {allocated} bytes for {text.Length} characters.");
        Assert.False(frame.IsWireMaterialized);
    }

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

    [Fact]
    public void OwnedGeneratedFrameKeepsTheCallerOwnedGraphWithoutFreezingIt()
    {
        var output = new List<object?> { new Dictionary<string, object?> { ["text"] = "large" } };
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "response.completed",
            ["response"] = new Dictionary<string, object?> { ["output"] = output }
        };

        var frame = SseFrame.FromOwnedJson(payload, "response.completed");

        Assert.Same(payload, frame.Event!.Data);
        Assert.Same(output, Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<Dictionary<string, object?>>(frame.Event.Data)["response"])["output"]);
        Assert.Contains("large", frame.WireText, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    public async Task ResponsesEventsRetainTheirYieldedSnapshotsAndSequence(string sourceProtocol)
    {
        var input = sourceProtocol == "chat"
            ? """
              [
                {"id":"c1","choices":[{"delta":{"content":"a","reasoning_content":"r"}}]},
                {"choices":[{"delta":{"content":"b","tool_calls":[{"index":0,"id":"f1","type":"function","function":{"name":"lookup","arguments":"{\"q\":"}}]}}]},
                {"choices":[{"delta":{"reasoning_content":"s","tool_calls":[{"index":0,"function":{"arguments":"1}"}}]},"finish_reason":"tool_calls"}]},
                "[DONE]"
              ]
              """
            : """
              [
                {"type":"message_start","message":{"id":"m1","content":[]}},
                {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},
                {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"a"}},
                {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"b"}},
                {"type":"content_block_stop","index":0},
                {"type":"content_block_start","index":1,"content_block":{"type":"thinking","thinking":"r","signature":"sig"}},
                {"type":"content_block_delta","index":1,"delta":{"type":"thinking_delta","thinking":"s"}},
                {"type":"content_block_stop","index":1},
                {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"f1","name":"lookup","input":{}}},
                {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"q\":"}},
                {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"1}"}},
                {"type":"content_block_stop","index":2},
                {"type":"message_delta","delta":{"stop_reason":"tool_use"}},
                {"type":"message_stop"}
              ]
              """;
        using var document = JsonDocument.Parse(input);
        var result = new ConvertedStreamResult();
        var events = sourceProtocol == "chat"
            ? SseStreamConverter.ChatToResponsesFrames(Source(), "visible", result, null, false, 7, 3, default)
            : SseStreamConverter.MessagesToResponsesFrames(Source(), "visible", result, null, false, 7, 3, default);
        var observed = new List<(SseFrame Frame, string Snapshot)>();
        var sequence = 7;
        await foreach (var frame in events)
        {
            var payload = Assert.IsType<Dictionary<string, object?>>(frame.Event!.Data);
            Assert.Equal(frame.Type, payload["type"]);
            Assert.Equal(sequence++, payload["sequence_number"]);
            Assert.False(frame.IsWireMaterialized);
            observed.Add((frame, JsonSerializer.Serialize(payload)));
        }
        Assert.Contains(observed, item => item.Frame.Type == "response.output_text.delta");
        Assert.Contains(observed, item => item.Frame.Type == "response.reasoning_summary_text.delta");
        Assert.Contains(observed, item => item.Frame.Type == "response.function_call_arguments.delta");
        var completed = result.UpstreamResponse!;
        completed["model"] = "mutated after conversion";
        await Task.WhenAll(observed.Select(item => Task.Run(() =>
        {
            Assert.Equal(item.Snapshot, JsonSerializer.Serialize(item.Frame.Event!.Data));
            AssertMatchesWire(item.Frame);
        })));
        var addedMessage = observed.Select(item => item.Frame)
            .First(frame => frame.Type == "response.output_item.added"
                && ((Dictionary<string, object?>)((Dictionary<string, object?>)frame.Event!.Data!)["item"]!)["type"] is "message");
        Assert.Equal("in_progress", ((Dictionary<string, object?>)
            ((Dictionary<string, object?>)addedMessage.Event!.Data!)["item"]!)["status"]);

        async IAsyncEnumerable<SseEvent> Source()
        {
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                    yield return new SseEvent("message", element.GetString());
                else
                {
                    var data = (Dictionary<string, object?>)ProtocolJson.FromJsonElement(element)!;
                    yield return new SseEvent(data.GetValueOrDefault("type") as string ?? "message", data);
                }
            }
            await Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MessagesToolArgumentsRetainTheirOriginalJsonEscaping(bool mcp)
    {
        var input = new Dictionary<string, object?> { ["data"] = "中<&>\"\\\n😀" };
        var expected = JsonSerializer.Serialize(input, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var result = new ConvertedStreamResult();
        var calls = new List<Dictionary<string, object?>>();
        await foreach (var frame in SseStreamConverter.MessagesToResponsesFrames(
            Source(), "visible", result, null, false, 0, 0, default))
        {
            var payload = (Dictionary<string, object?>)frame.Event!.Data!;
            if (frame.Type is "response.output_item.added" or "response.output_item.done"
                && payload["item"] is Dictionary<string, object?> item
                && item["type"] is "function_call" or "mcp_call"
                && (mcp || frame.Type == "response.output_item.done"))
            {
                calls.Add(item);
                Assert.Equal(expected, item["arguments"]);
            }
            if (frame.Type == "response.function_call_arguments.done")
                Assert.Equal(expected, payload["arguments"]);
        }
        Assert.NotEmpty(calls);

        async IAsyncEnumerable<SseEvent> Source()
        {
            yield return new SseEvent("message_start", new Dictionary<string, object?>
            {
                ["type"] = "message_start",
                ["message"] = new Dictionary<string, object?> { ["id"] = "m", ["content"] = new List<object?>() }
            });
            yield return new SseEvent("content_block_start", new Dictionary<string, object?>
            {
                ["type"] = "content_block_start", ["index"] = 0,
                ["content_block"] = new Dictionary<string, object?>
                {
                    ["type"] = mcp ? "mcp_tool_use" : "tool_use", ["id"] = "f",
                    ["name"] = "lookup", ["server_name"] = "server", ["input"] = input
                }
            });
            yield return new SseEvent("content_block_stop", new Dictionary<string, object?>
                { ["type"] = "content_block_stop", ["index"] = 0 });
            yield return new SseEvent("message_delta", new Dictionary<string, object?>
            {
                ["type"] = "message_delta",
                ["delta"] = new Dictionary<string, object?> { ["stop_reason"] = "tool_use" }
            });
            yield return new SseEvent("message_stop", new Dictionary<string, object?> { ["type"] = "message_stop" });
            await Task.CompletedTask;
        }
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

using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Streaming;
using OpenCodex.ProtocolConversion.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;
namespace OpenCodex.Api.Tests;
public sealed class SharedCaptureTextTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"ocx-shared-"+Guid.NewGuid().ToString("N"));
    private ProxyAuditSpool Spool()=>new(new(){Directory=_directory,MemoryThresholdBytes=0});
    [Fact]
    public void CachedPrefixAndDetachmentDoNotReopenTheSpool()
    {
        var spool = new SnapshotProbe(Spool());
        using var budget = new StreamCaptureBudget(1024 * 1024, 100, 100, spool, default);
        var source = budget.CreateText();
        budget.Append(source, "prefix and future");
        _ = source.ToString();
        var reads = spool.SnapshotReads;
        Assert.Equal("prefix", source.Prefix(6));
        var target = budget.CreateText();
        source.CopyPrefixTo(target, 6, budget);
        Assert.Equal(reads, spool.SnapshotReads);
        Assert.Equal("prefix", target.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrefixCopyPreservesSurrogatesAtCopyBoundaryAndTrailingHighSurrogate(bool strict)
    {
        using var budget = strict
            ? new StreamCaptureBudget(1024 * 1024, 100, 100, Spool(), default)
            : new StreamCaptureBudget(1024 * 1024);
        var source = budget.CreateText();
        var text = new string('x', ProtocolConversionParameters.Current.CaptureCopyCharacters - 1) + "😀tail\ud83d";
        budget.Append(source, text + "\ude00future");
        var target = budget.CreateText();
        source.CopyPrefixTo(target, text.Length, budget);
        Assert.Equal(text, target.ToString());
        Assert.Equal(text, source.Prefix(text.Length));
    }
    [Fact]
    public void ExactFieldLeaseUsesOneSpoolAndOneFinalImmutableString()
    {
        var spool=Spool();
        using(var budget=new StreamCaptureBudget(1024*1024,100,100,spool,default))
        {
            var source=budget.CreateText();var converter=new ConverterText(budget);var field=new object();
            for(var i=0;i<100;i++)
            {
                budget.BeginEvent();budget.AppendTracked(source,field,"content",new string('x',1024));
                converter.Add(new string('x',1024),budget.BorrowText(field,"content"));
                Assert.Null(budget.BorrowText(new object(),"content"));
            }
            Assert.Equal(2L*100*1024,spool.Usage.Bytes);
            Assert.Same(source.ToString(),converter.ToString());
            budget.BeginEvent();Assert.Null(budget.BorrowText(field,"content"));
        }
        Assert.Equal((0L,0),spool.Usage);
    }
    [Fact]
    public void InterleavedChoicesAndRefusalDoNotReorderOrDeduplicateEqualDeltas()
    {
        var spool=Spool();using var budget=new StreamCaptureBudget(1024,100,100,spool,default);
        var first=budget.CreateText();var second=budget.CreateText();var converter=new ConverterText(budget);
        var a=new object();var b=new object();
        budget.AppendTracked(first,a,"content","same");budget.AppendTracked(second,b,"content","same");
        converter.Add("same",budget.BorrowText(a,"content"));converter.Add("same",budget.BorrowText(b,"content"));
        converter.Add("refusal");Assert.Equal("samesamerefusal",converter.ToString());
    }
    [Fact]
    public void PrefixLeaseCannotReadFutureBytesAndResetDetachesBeforeDiscardingSource()
    {
        var spool=Spool();using var budget=new StreamCaptureBudget(1024,100,100,spool,default);
        var source=budget.CreateText();var converter=new ConverterText(budget);var field=new object();
        budget.AppendTracked(source,field,"delta","a\ud83d");converter.Add("a\ud83d",budget.BorrowText(field,"delta"));
        budget.BeginEvent();budget.AppendTracked(source,field,"delta","\ude00b");
        Assert.Equal("a\ud83d",converter.ToString());converter.Add("\ude00b",budget.BorrowText(field,"delta"));
        source.Clear();budget.Append(source,"different snapshot");Assert.Equal("a😀b",converter.ToString());
    }
    [Fact]
    public void RepeatedOwnerFieldWithinOneEventIsNotAnAmbiguousBorrowAndCancellationReleasesAll()
    {
        var spool=Spool();using var stop=new CancellationTokenSource();
        using(var budget=new StreamCaptureBudget(1024,100,100,spool,stop.Token))
        {
            var source=budget.CreateText();var field=new object();var converter=new ConverterText(budget);
            budget.AppendTracked(source,field,"text","x");budget.AppendTracked(source,field,"text","x");
            Assert.Null(budget.BorrowText(field,"text"));
            budget.BeginEvent();budget.AppendTracked(source,field,"text","tail");converter.Add("tail",budget.BorrowText(field,"text"));
            stop.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>converter.Add("late"));
        }
        Assert.Equal((0L,0),spool.Usage);
    }
    [Fact]
    public void ArgumentLeaseReadsOnlyNewAcceptedSuffixAndAuthoritativeSetDetaches()
    {
        var spool=Spool();using var budget=new StreamCaptureBudget(1024,100,100,spool,default);
        var source=budget.CreateText();var arguments=new ConverterArguments(budget);var owner=new object();
        budget.AppendTracked(source,owner,"arguments","{\"x\":\"");arguments.Append("{\"x\":\"",budget.BorrowText(owner,"arguments"));
        var offset=arguments.Length;budget.BeginEvent();budget.AppendTracked(source,owner,"arguments","a\"}");
        Assert.Equal("",arguments.Suffix(offset));arguments.Append("a\"}",budget.BorrowText(owner,"arguments"));Assert.Equal("a\"}",arguments.Suffix(offset));
        Assert.Equal(2L*source.Length,spool.Usage.Bytes);Assert.Same(source.ToString(),arguments.ToString());
        arguments.Set("{}");Assert.Equal("{}",arguments.ToString());Assert.Equal("{\"x\":\"a\"}",source.ToString());
    }
    [Fact]
    public async Task ChatFunctionConversionActuallySharesRawArgumentsUntilFinalImmutableBody()
    {
        var spool=Spool();using var budget=new StreamCaptureBudget(1024*1024,100,100,spool,default);using var capture=new StreamResponseCapture("chat",budget);
        var result=new ConvertedStreamResult{SharedCapture=capture};var content=new string('x',128*1024);var arguments="{\"x\":\""+content+"\"}";
        async IAsyncEnumerable<SseEvent> Events()
        {
            var payload=new Dictionary<string,object?>{["id"]="c",["choices"]=new List<object?>{new Dictionary<string,object?>{["index"]=0,["delta"]=new Dictionary<string,object?>{["tool_calls"]=new List<object?>{new Dictionary<string,object?>{["index"]=0,["id"]="tool-id",["type"]="function",["function"]=new Dictionary<string,object?>{["name"]="fn",["arguments"]=arguments}}}}}}};
            var item=new SseEvent("message",payload);capture.Accumulator.Accept(item);yield return item;
            // Only one copy of the large raw function-argument field is on disk.
            Assert.True(spool.Usage.Bytes<2L*arguments.Length+1024);
            var done=new SseEvent("message","[DONE]");capture.Accumulator.Accept(done);yield return done;await Task.CompletedTask;
        }
        IReadOnlyDictionary<string,object?>? terminal=null;
        await foreach(var frame in SseStreamConverter.ChatToResponsesFrames(Events(),"visible",result,null,false,0,0,default))
            if(frame.IsResponseCompleted)terminal=(IReadOnlyDictionary<string,object?>)((IReadOnlyDictionary<string,object?>)frame.Event!.Data!)["response"]!;
        var choices=(List<object?>)result.UpstreamResponse!["choices"]!;var message=(Dictionary<string,object?>)((Dictionary<string,object?>)choices[0]!)["message"]!;
        var calls=(List<object?>)message["tool_calls"]!;var function=(Dictionary<string,object?>)((Dictionary<string,object?>)calls[0]!)["function"]!;
        var output=(IReadOnlyList<object?>)terminal!["output"]!;var call=output.Cast<IReadOnlyDictionary<string,object?>>().Single(item=>Equals(item["type"],"function_call"));
        Assert.Same(function["arguments"],call["arguments"]);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResolvedToolArgumentsReuseCurrentFragmentAndReplayOnlyDeferredPrefix(bool custom, bool delayedName)
    {
        var spool = Spool();
        using var budget = new StreamCaptureBudget(1024 * 1024, 100, 100, spool, default);
        using var capture = new StreamResponseCapture("chat", budget);
        var result = new ConvertedStreamResult { SharedCapture = capture };
        var pieces = custom
            ? new[] { "first", "second", new string('x', 17) }
            : new[] { "{\"x\":\"", "second", new string('x', 17) + "\"}" };
        var deltas = new List<string>();
        await foreach (var frame in SseStreamConverter.ChatToResponsesFrames(
            Events(), "visible", result, null, false, 0, 0, default))
        {
            if (frame.Type is "response.function_call_arguments.delta" or "response.custom_tool_call_input.delta")
                deltas.Add((string)((IReadOnlyDictionary<string, object?>)frame.Event!.Data!)["delta"]!);
        }
        Assert.Equal(string.Concat(pieces), string.Concat(deltas));
        Assert.Same(pieces[^1], deltas[^1]);
        Assert.Equal(delayedName ? 2 : 3, deltas.Count);

        async IAsyncEnumerable<SseEvent> Events()
        {
            for (var index = 0; index < pieces.Length; index++)
            {
                var body = new Dictionary<string, object?>
                {
                    [custom ? "input" : "arguments"] = pieces[index]
                };
                if (!delayedName || index == 1) body["name"] = "tool";
                var call = new Dictionary<string, object?>
                {
                    ["index"] = 0, ["id"] = "call", ["type"] = custom ? "custom" : "function",
                    [custom ? "custom" : "function"] = body
                };
                var item = new SseEvent("message", new Dictionary<string, object?>
                {
                    ["choices"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["index"] = 0,
                            ["delta"] = new Dictionary<string, object?> { ["tool_calls"] = new List<object?> { call } }
                        }
                    }
                });
                capture.Accumulator.Accept(item);
                yield return item;
            }
            var done = new SseEvent("message", "[DONE]");
            capture.Accumulator.Accept(done);
            yield return done;
            await Task.CompletedTask;
        }
    }
    private sealed class SnapshotProbe(IProtocolAuditCapture inner) : IProtocolAuditCapture
    {
        internal int SnapshotReads;
        public int MemoryThresholdBytes => inner.MemoryThresholdBytes;
        public IProtocolAuditBody CaptureJson(object? value, CancellationToken cancellationToken) =>
            inner.CaptureJson(value, cancellationToken);
        public IProtocolAuditBuffer CreateStaging(CancellationToken cancellationToken) =>
            new Buffer(inner.CreateStaging(cancellationToken), this);
        private sealed class Buffer(IProtocolAuditBuffer inner, SnapshotProbe owner) : IProtocolAuditBuffer
        {
            public long Length => inner.Length;
            public void Write(ReadOnlySpan<byte> value) => inner.Write(value);
            public Stream OpenSnapshotRead() { owner.SnapshotReads++; return inner.OpenSnapshotRead(); }
            public void Dispose() => inner.Dispose();
        }
    }
    public void Dispose(){if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

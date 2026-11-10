using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class FullStreamCaptureTests : IDisposable
{
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"ocx-full-capture-"+Guid.NewGuid().ToString("N"));
    private ProxyAuditSpool Spool()=>new(new() { Directory=_dir,MemoryThresholdBytes=32768,MaxFiles=1024 });
    private static SseFrame Frame(string type,Dictionary<string,object?> payload) { payload["type"]=type;return SseFrame.FromJson(payload,type); }
    [Theory]
    [InlineData("chat")][InlineData("messages")][InlineData("responses")]
    public void FullCaptureExceedsOldOneMiBLimitWithoutTruncationAndKeepsUsage(string protocol)
    {
        var spool=Spool();var text=new string('x',4096);var expected=string.Concat(Enumerable.Repeat(text,300));
        using(var capture=new StreamResponseCapture(protocol,new StreamCaptureBudget(8*1024*1024,4096,8192,spool,default)))
        {
            if(protocol=="messages")capture.Accept(Frame("message_start",new() { ["message"]=new Dictionary<string,object?> { ["id"]="m",["model"]="visible",["usage"]=new Dictionary<string,object?> { ["input_tokens"]=7 } } }));
            if(protocol=="responses")capture.Accept(Frame("response.created",new() { ["response"]=new Dictionary<string,object?> { ["id"]="r",["status"]="in_progress" } }));
            for(var i=0;i<300;i++) capture.Accept(protocol switch
            {
                "chat"=>SseFrame.FromJson(new Dictionary<string,object?> { ["id"]="c",["choices"]=new object?[] { new Dictionary<string,object?> { ["index"]=0,["delta"]=new Dictionary<string,object?> { ["content"]=text } } } }),
                "messages"=>Frame("content_block_delta",new() { ["index"]=0,["delta"]=new Dictionary<string,object?> { ["type"]="text_delta",["text"]=text } }),
                _=>Frame("response.output_text.delta",new() { ["output_index"]=0,["content_index"]=0,["delta"]=text })
            });
            Assert.True(Directory.EnumerateFiles(_dir).Any());
            if(protocol=="chat") { capture.Accept(SseFrame.FromJson(new Dictionary<string,object?> { ["usage"]=new Dictionary<string,object?> { ["prompt_tokens"]=7,["completion_tokens"]=11 } }));capture.Accept(SseFrame.Done); }
            else if(protocol=="messages") { capture.Accept(Frame("message_delta",new() { ["usage"]=new Dictionary<string,object?> { ["output_tokens"]=11 } }));capture.Accept(Frame("message_stop",new())); }
            else capture.Accept(Frame("response.completed",new() { ["response"]=new Dictionary<string,object?> { ["id"]="r",["status"]="completed",["output"]=new List<object?>(),["usage"]=new Dictionary<string,object?> { ["input_tokens"]=7,["output_tokens"]=11 } } }));
            var completed=capture.Complete(StreamCaptureTermination.Completed);
            Assert.True(completed.Completed);Assert.False(completed.Truncated);Assert.DoesNotContain("_opencodex_capture",completed.Response!.Keys);
            Assert.Contains(expected,System.Text.Json.JsonSerializer.Serialize(completed.Response));
            var usage=Assert.IsType<Dictionary<string,object?>>(completed.Response["usage"]);
            Assert.Equal(11,usage[protocol=="chat"?"completion_tokens":"output_tokens"]);
        }
        Assert.Equal((0L,0),spool.Usage);Assert.Empty(Directory.EnumerateFiles(_dir));
    }
    [Fact]
    public void MoreThan256BlocksAreRetainedAndCollectionQuotaIsExplicitFailure()
    {
        var spool=Spool();
        using(var capture=new StreamResponseCapture("messages",new StreamCaptureBudget(1024*1024,1024,8192,spool,default)))
        {
            for(var i=0;i<300;i++)capture.Accept(Frame("content_block_start",new() { ["index"]=i,["content_block"]=new Dictionary<string,object?> { ["type"]="text",["text"]="x" } }));
            capture.Accept(Frame("message_stop",new()));
            Assert.Equal(300,Assert.IsType<List<object?>>(capture.Complete(StreamCaptureTermination.Completed).Response!["content"]).Count);
        }
        using(var limited=new StreamResponseCapture("messages",new StreamCaptureBudget(1024,1,100,spool,default)))
        {
            limited.Accept(Frame("content_block_start",new() { ["index"]=0,["content_block"]=new Dictionary<string,object?> { ["type"]="text" } }));
            Assert.Throws<UpstreamException>(()=>limited.Accept(Frame("content_block_start",new() { ["index"]=1,["content_block"]=new Dictionary<string,object?> { ["type"]="text" } })));
        }
        Assert.Equal((0L,0),spool.Usage);
    }
    [Fact]
    public void SplitSurrogatesAndRepeatedReadsPreserveTextWhileAppendRemainsPossible()
    {
        var spool=Spool();using var budget=new StreamCaptureBudget(1024,100,10,spool,default);
        var text=budget.CreateText();budget.Append(text,"a\ud83d");Assert.Equal("a\ud83d",text.ToString());
        budget.Append(text,"\ude00b");Assert.Equal("a😀b",text.ToString());
        Assert.Equal("a😀b",text.ToString());text.Clear();budget.Append(text,"next");Assert.Equal("next",text.ToString());
    }
    [Fact]
    public void CancellationAndQuotaReleaseFilesAndNeverBecomeSwallowedObserverFailure()
    {
        var spool=Spool();using var cancellation=new CancellationTokenSource();
        using(var capture=new StreamResponseCapture("responses",new StreamCaptureBudget(1024*1024,100,100,spool,cancellation.Token)))
        {
            capture.Accept(Frame("response.output_text.delta",new() { ["delta"]=new string('x',40000) }));
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(()=>capture.Accept(Frame("response.output_text.delta",new() { ["delta"]="late" })));
        }
        using(var capture=new StreamResponseCapture("responses",new StreamCaptureBudget(5,100,100,spool,default)))
            Assert.Throws<UpstreamException>(()=>capture.Accept(Frame("response.output_text.delta",new() { ["delta"]="too long" })));
        Assert.Equal((0L,0),spool.Usage);Assert.Empty(Directory.EnumerateFiles(_dir));
    }
    [Fact]
    public void ResponsesSparseContentIndexesCannotBeSilentlyTakenAwayAtBuildTime()
    {
        var spool=Spool();using var capture=new StreamResponseCapture("responses",new StreamCaptureBudget(10000,1,100,spool,default));
        capture.Accept(Frame("response.output_text.delta",new() { ["content_index"]=3,["delta"]="one" }));
        Assert.Throws<UpstreamException>(()=>capture.Accept(Frame("response.refusal.delta",new() { ["content_index"]=7,["delta"]="two" })));
    }
    [Fact]
    public void DiskFailureIsExplicitAndAppendReservationsAreReleased()
    {
        var spool=new ProxyAuditSpool(new() { Directory=_dir,MemoryThresholdBytes=0 })
        { CreateFile=(_,_)=>throw new IOException("simulated ENOSPC") };
        using(var capture=new StreamResponseCapture("responses",new StreamCaptureBudget(10000,100,100,spool,default)))
            Assert.Throws<IOException>(()=>capture.Accept(Frame("response.output_text.delta",new() { ["delta"]="data" })));
        Assert.Equal((0L,0),spool.Usage);Assert.Empty(Directory.EnumerateFiles(_dir));
    }
    public void Dispose() { if(Directory.Exists(_dir))Directory.Delete(_dir,true); }
}

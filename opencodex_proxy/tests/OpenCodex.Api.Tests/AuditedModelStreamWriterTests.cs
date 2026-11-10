using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D=System.Collections.Generic.Dictionary<string,object?>;

namespace OpenCodex.Api.Tests;

public sealed class AuditedModelStreamWriterTests
{
    [Fact]
    public async Task AuditedBatchBoundaryIsNotEndOfStreamAndTrackingRetainsCompletedItemsUntilTerminal()
    {
        var seen=new List<string>();var audited=false;
        var writer=new MultiAgentModelStreamWriter((item,ct)=>{var type=JsonDictionaryValue.String(item,"type");if(type=="response.completed")Assert.True(audited);seen.Add(type);return Task.CompletedTask;});
        var tracking=new TrackingProxyStreamWriter(writer);
        await tracking.WriteFrameBatchAsync(Frames(new D{["type"]="response.output_item.done",["output_index"]=0,["item"]=new D{["type"]="message",["content"]=new List<object?>{new D{["type"]="output_text",["text"]="complete"}}}}),_=>false,()=>0);
        Assert.Null(writer.Result);Assert.True(tracking.HasWritten);Assert.Single(seen);
        await tracking.WriteFrameBatchAsync(Frames(),_=>false,()=>0);
        audited=true;
        await tracking.WriteFramesAsync(Frames(new D{["type"]="response.completed",["response"]=new D{["id"]="r1",["status"]="completed",["output"]=new List<object?>()}}),_=>false,()=>0);
        Assert.Single(JsonDictionaryValue.List(writer.Result!,"output"));Assert.Equal(new[]{"response.output_item.done","response.completed"},seen);
    }
    [Fact]
    public async Task InvalidEmptyCompletedIsRejectedBeforeCallbackOrResultPublication()
    {
        var sent=0;var writer=new MultiAgentModelStreamWriter((item,ct)=>{sent++;return Task.CompletedTask;});
        await Assert.ThrowsAsync<UpstreamException>(()=>writer.WriteFramesAsync(Frames(new D{["type"]="response.completed",["response"]=new D{["status"]="completed",["output"]=new List<object?>()}}),_=>false,()=>0));
        Assert.Equal(0,sent);Assert.Null(writer.Result);
    }
    private static async IAsyncEnumerable<SseFrame> Frames(params D[] events)
    {foreach(var item in events){yield return SseFrame.FromJson(item,JsonDictionaryValue.String(item,"type"));await Task.CompletedTask;}}
}

using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Protocols;
using Xunit;
namespace OpenCodex.Api.Tests;
public sealed class GeneratedUtf8WriterTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void SegmentedUtf8MatchesLegacyUnicodeWireWithoutMaterializingIt(bool raw)
    {
        var original=SseFrame.FromJson(new Dictionary<string,object?>{["text"]=string.Concat(Enumerable.Repeat("a\n\"😀中<>",10000))},"response.completed");
        var frame=raw?new SseFrame(original.WireText,original.Event):original;
        var output=new Probe();var length=frame.WriteUtf8(output);
        if(!raw)Assert.False(frame.IsWireMaterialized);
        Assert.True(output.MaxHint<=16*1024);Assert.Equal(length,output.Buffer.WrittenCount);
        Assert.Equal(frame.WireText,Encoding.UTF8.GetString(output.Buffer.WrittenSpan));
    }
    [Fact]
    public async Task ActualHttpWriterDoesNotMaterializeLargeGeneratedTerminalForEncodingOrNeverTtft()
    {
        var frame=SseFrame.FromJson(new Dictionary<string,object?>{["type"]="response.completed",["response"]=new Dictionary<string,object?>{["status"]="completed",["output"]=new string('x',512*1024)}},"response.completed");
        var context=new DefaultHttpContext();context.Response.Body=new MemoryStream();
        await ProxyStreamResponseWriter.WriteFramesAsync(context.Response,Frames(frame),SseFramePredicate.Never,()=>0);
        Assert.False(frame.IsWireMaterialized);Assert.True(context.Response.Body.Length>512*1024);
    }
    [Fact]
    public void ArbitraryExtensionTtftPredicateStillReceivesItsExactWire()
    {
        var frame=SseFrame.FromJson(new Dictionary<string,object?>{["text"]=new string('x',100000)});
        string? seen=null;Assert.True(SseFramePredicate.Evaluate(wire=>{seen=wire;return true;},frame));Assert.Same(frame.WireText,seen);
    }
    [Fact]
    public void TypedTtftKeepsSubstringAndMulticastCompatibilityWithoutLargeWire()
    {
        var frame=SseFrame.FromJson(new Dictionary<string,object?>{["text"]=new string('x',100000)+"response.output_item.done"});
        Assert.True(SseFramePredicate.Evaluate(SseStreamConverter.FrameCountsForTtft,frame));Assert.False(frame.IsWireMaterialized);
        var calls=0;Func<string,bool> callback=_=>{calls++;return true;};callback+=SseFramePredicate.Never;
        Assert.False(SseFramePredicate.Evaluate(callback,frame));Assert.Equal(1,calls);Assert.True(frame.IsWireMaterialized);
    }
    private static async IAsyncEnumerable<SseFrame> Frames(SseFrame frame){yield return frame;await Task.CompletedTask;}
    private sealed class Probe:IBufferWriter<byte>
    {
        internal readonly ArrayBufferWriter<byte> Buffer=new();internal int MaxHint;
        public void Advance(int count)=>Buffer.Advance(count);
        public Memory<byte> GetMemory(int sizeHint=0){MaxHint=Math.Max(MaxHint,sizeHint);return Buffer.GetMemory(sizeHint);}
        public Span<byte> GetSpan(int sizeHint=0){MaxHint=Math.Max(MaxHint,sizeHint);return Buffer.GetSpan(sizeHint);}
    }
}

using System.Net.WebSockets;
using System.Text.Json;
using OpenCodex.Core.Protocols;
using Xunit;
namespace OpenCodex.Api.Tests;
public sealed class WebSocketJsonWriterTests
{
    [Fact]
    public async Task LargeUnicodeDefaultJsonMatchesExactlyAndHasOneMessageBoundary()
    {
        var payload=new Dictionary<string,object?>{["text"]=string.Concat(Enumerable.Repeat("\"\n中😀<>&\u0000",10000)),["nested"]=new List<object?>{1,true,null,2.5}};
        using var socket=new Capture();await WebSocketJsonWriter.SendAsync(socket,payload,4*1024*1024,default);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(payload),socket.Body.ToArray());Assert.Equal(1,socket.Ends);Assert.True(socket.Frames>1);Assert.True(socket.Largest<=32*1024);
    }
    [Fact]
    public async Task OversizeIsRejectedBeforeAnyMessageFragmentIsAttempted()
    {
        using var socket=new Capture();await Assert.ThrowsAsync<InvalidDataException>(()=>WebSocketJsonWriter.SendAsync(socket,new Dictionary<string,object?>{["text"]=new string('中',100000)},1024,default));Assert.Equal(0,socket.Frames);
    }
    [Fact]
    public async Task SlowSendBackpressuresEncoderAndCancellationNeverEndsMessage()
    {
        using var stop=new CancellationTokenSource();using var socket=new Capture();var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.Block=async ct=>{first.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,ct);};
        var operation=WebSocketJsonWriter.SendAsync(socket,new Dictionary<string,object?>{["text"]=new string('x',1024*1024)},4*1024*1024,stop.Token);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(1,socket.Frames);Assert.False(operation.IsCompleted);stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>operation);Assert.Equal(0,socket.Ends);
    }
    private sealed class Capture:WebSocket
    {
        internal readonly MemoryStream Body=new();internal int Frames,Ends,Largest;internal Func<CancellationToken,Task>? Block;
        public override WebSocketCloseStatus? CloseStatus=>null;public override string? CloseStatusDescription=>null;public override WebSocketState State=>WebSocketState.Open;public override string? SubProtocol=>null;
        public override async Task SendAsync(ArraySegment<byte> buffer,WebSocketMessageType messageType,bool endOfMessage,CancellationToken ct)
        {Frames++;Largest=Math.Max(Largest,buffer.Count);if(Block is not null)await Block(ct);Body.Write(buffer);if(endOfMessage)Ends++;}
        public override void Abort(){}public override void Dispose()=>Body.Dispose();
        public override Task CloseAsync(WebSocketCloseStatus status,string? message,CancellationToken ct)=>Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status,string? message,CancellationToken ct)=>Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken ct)=>throw new NotSupportedException();
    }
}

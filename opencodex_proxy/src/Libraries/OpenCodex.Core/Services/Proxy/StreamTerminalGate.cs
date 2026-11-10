using System.Runtime.CompilerServices;
using OpenCodex.Core.Protocols;
namespace OpenCodex.Core.Services.Proxy;

/// <summary>One protocol terminal plus optional Responses [DONE]. No whole-round collection.
/// Failure before Release discards success; callers must propagate the failure/close the stream.</summary>
internal sealed class StreamTerminalGate
{
    private SseFrame? _terminal;
    private bool _done;
    internal bool HasCommitted { get; private set; }
    internal void Fail(string protocol,string message,string? code)
    {
        if(_terminal?.IsError==true)return;
        var error=new Dictionary<string,object?> { ["type"]="api_error",["message"]=message,["code"]=code??"upstream_stream_error" };
        var payload=protocol==ProtocolConverter.Responses
            ? new Dictionary<string,object?> { ["type"]="error",["message"]=message,["code"]=code??"upstream_stream_error" }
            : new Dictionary<string,object?> { ["error"]=error };
        if(protocol==ProtocolConverter.Messages)payload["type"]="error";
        _terminal=SseFrame.FromJson(payload,protocol==ProtocolConverter.Chat?null:"error");_done=false;
    }
    internal async IAsyncEnumerable<SseFrame> Hold(IAsyncEnumerable<SseFrame> frames,[EnumeratorCancellation] CancellationToken ct)
    {
        await foreach(var frame in frames.WithCancellation(ct))
        {
            if(_terminal is null)
            {
                if(frame.IsTerminal)_terminal=frame;
                else { if(frame.IsSemantic)HasCommitted=true;yield return frame; }
                continue;
            }
            if(frame.IsDone && !_terminal.IsDone && !_done) { _done=true;continue; }
            throw new InvalidDataException("Unexpected event after the stream terminal; success was not published.");
        }
    }
    internal async IAsyncEnumerable<SseFrame> Release([EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(_terminal is { } terminal)
        {
            _terminal=null;yield return terminal;
            if(_done) { _done=false;yield return SseFrame.Done; }
        }
        await Task.CompletedTask;
    }
}

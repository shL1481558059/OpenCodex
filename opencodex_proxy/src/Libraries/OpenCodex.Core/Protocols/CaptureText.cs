using System.Runtime.InteropServices;
using System.Text;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Core.Protocols;

/// <summary>Raw UTF16 private append storage preserves even split surrogate deltas exactly.
/// No prefix materialization during append; ordinary JSON encoding happens only in the final
/// immutable audit snapshot. Reads never seal the append buffer (tool names may be fragmented).</summary>
internal sealed class CaptureText(ProxyAuditSpool? spool,CancellationToken cancellationToken) : IDisposable
{
    private StringBuilder? _memory;
    private ProxyAuditSpool.AuditFile? _buffer;
    private int _length;
    private string? _cached;
    public int Length=>_length;
    public void Append(string value)=>Append(value.AsSpan());
    public void Append(ReadOnlySpan<char> value)
    {
        if(value.IsEmpty)return;
        cancellationToken.ThrowIfCancellationRequested();
        if(spool is null) (_memory??=new()).Append(value);
        else (_buffer??=spool.CreateStaging(cancellationToken)).Write(MemoryMarshal.AsBytes(value));
        _length=checked(_length+value.Length);_cached=null;
    }
    internal void AppendLegacy(StreamCaptureBudget budget,string fragment)
    { budget.Append(_memory??=new(),fragment);_length=_memory.Length;_cached=null; }
    public void Clear() { _buffer?.Dispose();_buffer=null;_memory=null;_length=0;_cached=null; }
    public override string ToString()
    {
        if(_cached is not null)return _cached;
        if(_buffer is null)return _cached=_memory?.ToString() ?? "";
        using var read=_buffer.OpenSnapshotRead();
        return _cached=string.Create(_length,read,static (span,source)=>source.ReadExactly(MemoryMarshal.AsBytes(span)));
    }
    public void Dispose()=>Clear();
}

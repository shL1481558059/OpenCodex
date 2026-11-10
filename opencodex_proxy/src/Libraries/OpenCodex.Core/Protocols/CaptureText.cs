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
    internal event Action? Resetting;
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
    public void Clear() { Resetting?.Invoke(); Release(); }
    private void Release() { _buffer?.Dispose();_buffer=null;_memory=null;_length=0;_cached=null; }
    internal string Prefix(int length)
    {
        if(length==_length)return ToString();
        using var source=OpenRawRead();
        return string.Create(length,source,static(span,read)=>read.ReadExactly(MemoryMarshal.AsBytes(span)));
    }
    internal void CopyPrefixTo(CaptureText target,int length,StreamCaptureBudget budget)
    {
        using var source=OpenRawRead();var chars=new char[Math.Min(length,8192)];
        while(length>0)
        {
            var count=Math.Min(length,chars.Length);source.ReadExactly(MemoryMarshal.AsBytes(chars.AsSpan(0,count)));
            if(count<length && char.IsHighSurrogate(chars[count-1])){source.Position-=2;count--;}
            budget.Append(target,new string(chars,0,count));length-=count;
        }
    }
    public override string ToString()
    {
        if(_cached is not null)return _cached;
        if(_buffer is null)return _cached=_memory?.ToString() ?? "";
        using var read=_buffer.OpenSnapshotRead();
        return _cached=string.Create(_length,read,static (span,source)=>source.ReadExactly(MemoryMarshal.AsBytes(span)));
    }
    internal Stream OpenRawRead()
    {
        if(_buffer is not null)return _buffer.OpenSnapshotRead();
        var value=_memory?.ToString()??"";
        return new MemoryStream(MemoryMarshal.AsBytes(value.AsSpan()).ToArray(),writable:false);
    }
    internal bool IsCached(string value)=>ReferenceEquals(_cached,value);
    internal string ReadSuffix(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if(offset>_length)throw new ArgumentOutOfRangeException(nameof(offset));
        if(offset==_length)return "";
        if(_cached is not null)return _cached[offset..];
        if(_buffer is null)return _memory?.ToString(offset,_length-offset)??"";
        using var read=_buffer.OpenSnapshotRead();read.Position=2L*offset;
        return string.Create(_length-offset,read,static(span,source)=>source.ReadExactly(MemoryMarshal.AsBytes(span)));
    }
    public void Dispose() { Resetting=null;Release(); }
}

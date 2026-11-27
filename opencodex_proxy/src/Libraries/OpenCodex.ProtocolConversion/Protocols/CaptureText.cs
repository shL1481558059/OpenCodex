using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using OpenCodex.ProtocolConversion.Abstractions;

namespace OpenCodex.ProtocolConversion;

/// <summary>Raw UTF16 private append storage preserves even split surrogate deltas exactly.
/// No prefix materialization during append; ordinary JSON encoding happens only in the final
/// immutable audit snapshot. Reads never seal the append buffer (tool names may be fragmented).</summary>
internal sealed class CaptureText(IProtocolAuditCapture? spool,CancellationToken cancellationToken) : IDisposable
{
    private StringBuilder? _memory;
    private IProtocolAuditBuffer? _buffer;
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
    internal void AppendLegacy(StreamCaptureBudget budget,ReadOnlySpan<char> fragment)
    { budget.Append(_memory??=new(),fragment);_length=_memory.Length;_cached=null; }
    public void Clear() { Resetting?.Invoke(); Release(); }
    private void Release() { _buffer?.Dispose();_buffer=null;_memory=null;_length=0;_cached=null; }
    internal string Prefix(int length)
    {
        if(length==_length)return ToString();
        if(_cached is not null)return _cached[..length];
        if(_buffer is null)return _memory?.ToString(0,length) ?? "";
        using var source=OpenRawRead();
        return string.Create(length,source,static(span,read)=>read.ReadExactly(MemoryMarshal.AsBytes(span)));
    }
    internal void CopyPrefixTo(CaptureText target,int length,StreamCaptureBudget budget)
    {
        if(length==0)return;
        var copyCharacters=ProtocolConversionParameters.Current.CaptureCopyCharacters;
        var chars=ArrayPool<char>.Shared.Rent(Math.Min(length,copyCharacters));
        using var source=_cached is null ? _buffer?.OpenSnapshotRead() : null;
        try
        {
            var offset=0;
            while(length>0)
            {
                var count=Math.Min(length,copyCharacters);
                var span=chars.AsSpan(0,count);
                if(source is not null)source.ReadExactly(MemoryMarshal.AsBytes(span));
                else if(_cached is not null)_cached.AsSpan(offset,count).CopyTo(span);
                else _memory!.CopyTo(offset,span,count);
                if(count<length && char.IsHighSurrogate(chars[count-1]))
                {
                    if(source is not null)source.Position-=2;
                    count--;
                }
                budget.Append(target,chars.AsSpan(0,count));length-=count;offset+=count;
            }
        }
        finally { ArrayPool<char>.Shared.Return(chars,clearArray:true); }
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
    internal string ReadSuffix(int offset)=>ReadRange(offset,_length-offset);
    internal string ReadRange(int offset,int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);ArgumentOutOfRangeException.ThrowIfNegative(length);
        if(offset>_length-length)throw new ArgumentOutOfRangeException(nameof(length));
        if(length==0)return "";
        if(offset==0&&length==_length)return ToString();
        if(_cached is not null)return _cached.Substring(offset,length);
        if(_buffer is null)return _memory?.ToString(offset,length)??"";
        using var read=_buffer.OpenSnapshotRead();read.Position=2L*offset;
        return string.Create(length,read,static(span,source)=>source.ReadExactly(MemoryMarshal.AsBytes(span)));
    }
    public void Dispose() { Resetting=null;Release(); }
}

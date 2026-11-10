using System.Collections;
namespace OpenCodex.Core.Protocols;

/// <summary>A logical final text value, not a retained list of every upstream fragment.
/// Enumeration materializes once at final protocol reconstruction. No caller may depend
/// on original fragment boundaries (deltas are emitted directly from the current event).</summary>
internal readonly record struct CapturedTextSlice(CaptureText Source,int Offset,int Length);

// A request-owned, prefix-only read lease on an exact upstream field. Contiguous
// slices share its spool/cache. Interleaved fields detach once, preserving arrival
// order; destructive reset detaches BEFORE the upstream owner releases its bytes.
internal sealed class ConverterText(StreamCaptureBudget budget):IReadOnlyCollection<string>
{
    private CaptureText? _owned;
    private CaptureText? _shared;
    private int _length;
    internal int Length=>_length;
    internal string Suffix(int offset)=>_shared?.ReadRange(offset,_length-offset)??_owned?.ReadSuffix(offset)??(offset==0?"":throw new ArgumentOutOfRangeException(nameof(offset)));
    internal bool IsCached(string value)=>_shared is {} source?source.Length==_length&&source.IsCached(value):_owned?.IsCached(value) is true;
    internal void Reset(){if(_shared is {} source)source.Resetting-=Detach;_shared=null;_owned?.Clear();_length=0;}
    public int Count=>_length==0?0:1;
    public void Add(string value)=>Add(value,null);
    internal void Add(string value,CapturedTextSlice? slice)
    {
        if(value.Length==0)return;
        if(_owned is null && slice is { } view && view.Length==value.Length
            && view.Offset==_length && (_shared is null&&_length==0 || ReferenceEquals(_shared,view.Source)))
        {
            if(_shared is null){_shared=view.Source;_shared.Resetting+=Detach;}
            _length=checked(_length+view.Length);return;
        }
        Detach();_owned??=budget.CreateText();budget.Append(_owned,value);_length=_owned.Length;
    }
    private void Detach()
    {
        if(_shared is not {} source)return;
        _shared=null;source.Resetting-=Detach;
        _owned??=budget.CreateText();source.CopyPrefixTo(_owned,_length,budget);
    }
    public override string ToString()=>_shared?.Prefix(_length)??_owned?.ToString()??"";
    public IEnumerator<string> GetEnumerator() { if(Count!=0)yield return ToString(); }
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}

internal sealed class ConverterArguments(StreamCaptureBudget budget)
{
    private readonly ConverterText _text=new(budget);
    internal int Length=>_text.Length;
    internal void Append(string value,CapturedTextSlice? slice=null)=>_text.Add(value,slice);
    internal string Suffix(int offset)=>_text.Suffix(offset);
    internal void Set(string value) { if(_text.IsCached(value))return;_text.Reset();_text.Add(value); }
    public override string ToString()=>_text.ToString();
}

// Deferred tool argument replay must preserve original event fragmentation/order. Store the
// record lengths in the same spillable stream, never an in-memory per-delta offsets list.
internal sealed class ConverterFragments(StreamCaptureBudget budget):IReadOnlyCollection<string>
{
    private readonly CaptureText _records=budget.CreateText();
    public int Count { get; private set; }
    internal void Add(string value)
    {
        var header=string.Create(2,value.Length,static(chars,length)=>System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars),length));
        budget.Append(_records,header);budget.Append(_records,value);Count=checked(Count+1);
    }
    public IEnumerator<string> GetEnumerator()
    {
        using var read=_records.OpenRawRead();var header=new byte[4];
        for(var i=0;i<Count;i++)
        {
            read.ReadExactly(header);var length=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
            if(length<0 || 2L*length>read.Length-read.Position)throw new InvalidDataException("Corrupted deferred event record length.");
            yield return string.Create(length,read,static(chars,source)=>source.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars)));
        }
    }
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}

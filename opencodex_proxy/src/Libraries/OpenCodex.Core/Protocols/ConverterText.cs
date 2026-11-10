using System.Collections;
namespace OpenCodex.Core.Protocols;

/// <summary>A logical final text value, not a retained list of every upstream fragment.
/// Enumeration materializes once at final protocol reconstruction. No caller may depend
/// on original fragment boundaries (deltas are emitted directly from the current event).</summary>
internal sealed class ConverterText(StreamCaptureBudget budget):IReadOnlyCollection<string>
{
    private readonly CaptureText _text=budget.CreateText();
    public int Count=>_text.Length==0?0:1;
    public void Add(string value)=>budget.Append(_text,value);
    public override string ToString()=>_text.ToString();
    public IEnumerator<string> GetEnumerator() { if(Count!=0)yield return ToString(); }
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}

internal sealed class ConverterArguments(StreamCaptureBudget budget)
{
    private readonly CaptureText _text=budget.CreateText();
    internal int Length=>_text.Length;
    internal void Append(string value)=>budget.Append(_text,value);
    internal string Suffix(int offset)=>_text.ReadSuffix(offset);
    internal void Set(string value) { if(_text.IsCached(value))return;_text.Clear();budget.Append(_text,value); }
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

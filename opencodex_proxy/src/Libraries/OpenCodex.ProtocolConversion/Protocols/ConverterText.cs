using System.Collections;
using System.Text.Json;
using OpenCodex.ProtocolConversion.Abstractions;
using D = System.Collections.Generic.Dictionary<string, object?>;
namespace OpenCodex.ProtocolConversion;

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

internal sealed class CaptureBinary(StreamCaptureBudget budget) : Stream
{
    private MemoryStream? _memory;
    private IProtocolAuditBuffer? _buffer;

    internal void Append(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return;
        if (_buffer is null)
        {
            _memory ??= new MemoryStream();
            if (budget.Spool is { } spool && _memory.Length + value.Length > budget.MemoryThresholdBytes)
            {
                _buffer = spool.CreateStaging(budget.CancellationToken);
                _memory.Position = 0;
                _buffer.Write(_memory.GetBuffer().AsSpan(0, checked((int)_memory.Length)));
                _memory.Dispose();
                _memory = null;
            }
        }
        if (_buffer is not null) _buffer.Write(value);
        else _memory!.Write(value);
    }

    internal Stream OpenSnapshotRead()
    {
        if (_buffer is not null) return _buffer.OpenSnapshotRead();
        return new MemoryStream(_memory?.GetBuffer() ?? [], 0, checked((int)(_memory?.Length ?? 0)), writable: false);
    }

    public override void Write(ReadOnlySpan<byte> buffer) => Append(buffer);
    public override void Write(byte[] buffer, int offset, int count) => Append(buffer.AsSpan(offset, count));
    public override void WriteByte(byte value) => Append([value]);
    public override void Flush() { }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _buffer?.Length ?? _memory?.Length ?? 0;
    public override long Position { get => Length; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _memory?.Dispose();
            _buffer?.Dispose();
        }
        _memory = null;
        _buffer = null;
        base.Dispose(disposing);
    }
}

internal sealed class ConverterJsonFragments
{
    private readonly StreamCaptureBudget _budget;
    private readonly CaptureBinary _journal;

    internal ConverterJsonFragments(StreamCaptureBudget budget)
    {
        _budget = budget;
        _journal = budget.CreateBinary();
    }

    internal void Add(bool buffered, object? value)
    {
        _journal.WriteByte((byte)(buffered ? '1' : '0'));
        using (var writer = new System.Text.Json.Utf8JsonWriter(_journal))
        {
            Json.ProtocolJsonWriter.Write(writer, value, 0);
            writer.Flush();
        }
        _journal.WriteByte((byte)'\n');
        var current = _journal.Length;
        _budget.Spend(current - _budgetSpent);
        _budgetSpent = current;
    }

    private long _budgetSpent;

    internal IEnumerable<D> Read(bool onlyBuffered)
    {
        using var cursor = new JournalCursor(_journal.OpenSnapshotRead());
        while (cursor.TryReadByte(out var flag))
        {
            if (flag != '1' && onlyBuffered)
            {
                cursor.SkipLine();
                continue;
            }
            using var document = JsonDocument.Parse(cursor.CreateLineStream());
            yield return (D)Json.ProtocolJsonWriter.FromJsonElement(document.RootElement)!;
        }
    }

    private sealed class JournalCursor(Stream source) : IDisposable
    {
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _offset;
        private int _count;

        internal bool TryReadByte(out byte value)
        {
            if (_offset == _count && !Fill()) { value = 0; return false; }
            value = _buffer[_offset++];
            return true;
        }

        internal void SkipLine()
        {
            while (TryReadByte(out var value))
                if (value == '\n') return;
        }

        internal Stream CreateLineStream() => new LineReadStream(this);

        private bool Fill()
        {
            _count = source.Read(_buffer, 0, _buffer.Length);
            _offset = 0;
            return _count > 0;
        }

        private sealed class LineReadStream(JournalCursor cursor) : Stream
        {
            private bool _done;
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override int Read(Span<byte> buffer)
            {
                if (_done || buffer.IsEmpty) return 0;
                var written = 0;
                while (written < buffer.Length && cursor.TryReadByte(out var value))
                {
                    if (value == '\n')
                    {
                        _done = true;
                        break;
                    }
                    buffer[written++] = value;
                }
                return written;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        public void Dispose() => source.Dispose();
    }
}

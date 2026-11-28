using System.Buffers.Binary;
using System.Security.Cryptography;
using OpenCodex.ProtocolConversion;

namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// Creates request-scoped snapshot owners over the shared audit spool. This type does not
/// impose a byte or file-count admission limit; actual allocation still uses bounded pages
/// and explicit ownership for safe cleanup.
/// </summary>
public sealed class SnapshotStore
{
    // Binary layout sizes, not capacity defaults: identity, counters, state and SHA-256 address.
    internal const int ReservationMetadataBytes = 16 + sizeof(long) + sizeof(int);
    internal const int BufferMetadataBytes = 16 + 3 * sizeof(long) + 2 * sizeof(int) + SHA256.HashSizeInBytes;
    internal const int ContainerMetadataFieldsBytes = 16 + 3 * sizeof(long) + 2 * sizeof(int);
    internal const int PageHeaderBytes = sizeof(long);
    private readonly ProxyAuditSpool _spool;

    public SnapshotStore(ProxyAuditSpool sharedSpool)
    {
        ArgumentNullException.ThrowIfNull(sharedSpool);
        _spool = sharedSpool;
        PageBytes = ProtocolConversionParameters.Current.FlushBytes;
    }

    public int PageBytes { get; }
    public (long PhysicalBytes, long MetadataBytes, int Files, int Containers) Usage => _spool.SnapshotUsage;
    internal ProxyAuditSpool Spool => _spool;
    internal long ContainerMetadataBytes => _spool.SnapshotContainerMetadataBytes;
    internal long PhysicalPageBytes => checked((long)PageBytes + PageHeaderBytes);

    public SnapshotReservation Reserve(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reservation = new SnapshotReservation(this, cancellationToken);
        _spool.InitializeReservation(reservation);
        return reservation;
    }
}

/// <summary>One request/lifetime area. Pages are allocated on demand and physically shared by fields.</summary>
internal sealed class SnapshotContainer
{
    private readonly object _gate = new();
    private readonly SnapshotStore _store;
    private readonly SnapshotReservation _reservation;
    private readonly string _path;
    private readonly long _metadataBytes;
    private Stream? _writer;
    private long _physicalBytes;
    private long _freePage = -1;
    private int _buffers;
    private bool _ownerReleased, _disposed, _faulted;
    private IOException? _storageFailure;

    internal SnapshotContainer(SnapshotStore store, SnapshotReservation reservation)
    {
        _store = store;
        _reservation = reservation;
        _metadataBytes = store.ContainerMetadataBytes;
        try { _path = store.Spool.AllocateSnapshotPath(_metadataBytes); }
        catch (IOException exception) { throw SnapshotPersistenceException.Wrap(exception); }
    }

    internal string PathForTests => _path;
    internal int PageBytes => _store.PageBytes;
    internal long PhysicalPageBytes => _store.PhysicalPageBytes;

    internal SnapshotBuffer CreateBuffer()
    {
        lock (_gate)
        {
            EnsureWritable();
            _store.Spool.ConsumeSnapshot(SnapshotStore.BufferMetadataBytes, metadata: true);
            try
            {
                var buffer = new SnapshotBuffer(this);
                _buffers = checked(_buffers + 1);
                return buffer;
            }
            catch
            {
                _store.Spool.RefundSnapshot(SnapshotStore.BufferMetadataBytes, metadata: true);
                throw;
            }
        }
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed || _ownerReleased, this);
        _reservation.ThrowIfDisposed();
        if (_storageFailure is { } failure) throw failure;
        if (_faulted) throw new InvalidOperationException("Snapshot capture has failed; the body cannot be sealed.");
    }

    private Stream Writer => _writer ??= _store.Spool.CreateSnapshotStorage(_path);

    private long AllocatePage()
    {
        var writer = Writer;
        if (_freePage >= 0)
        {
            var page = _freePage;
            _freePage = ReadLink(writer, page);
            WriteLink(writer, page, -1);
            return page;
        }
        var offset = _physicalBytes;
                _store.Spool.ConsumeSnapshot(PhysicalPageBytes, metadata: false);
        var length = checked(offset + PhysicalPageBytes);
        // Retain the offered allocation charge on IO failure until deletion is proven. A failed
        // SetLength/Write may have grown the private file partially; never refund around it.
        _physicalBytes = length;
        writer.SetLength(length);
        WriteLink(writer, offset, -1);
        return offset;
    }

    internal void Write(SnapshotBuffer buffer, ReadOnlySpan<byte> value)
    {
        lock (_gate)
        {
            EnsureWritable();
            try
            {
                while (!value.IsEmpty)
                {
                    if (buffer.LastPage < 0 || buffer.LastPageBytes == PageBytes)
                    {
                        var page = AllocatePage();
                        if (buffer.LastPage < 0) buffer.FirstPage = page;
                        else WriteLink(Writer, buffer.LastPage, page);
                        buffer.LastPage = page;
                        buffer.LastPageBytes = 0;
                    }
                    var count = Math.Min(value.Length, PageBytes - buffer.LastPageBytes);
                    Writer.Position = checked(buffer.LastPage + SnapshotStore.PageHeaderBytes + buffer.LastPageBytes);
                    Writer.Write(value[..count]);
                    buffer.LastPageBytes += count;
                    buffer.ByteLength = checked(buffer.ByteLength + count);
                    value = value[count..];
                }
            }
            catch (IOException exception)
            {
                _faulted = true; _storageFailure = SnapshotPersistenceException.Wrap(exception);
                throw _storageFailure;
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
    }

    internal Stream OpenReader(SnapshotBuffer buffer, long firstPage, long length)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Stream? source = null;
            try
            {
                _writer?.Flush();
                if (length > 0)
                    source = new FileStream(_path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.RandomAccess);
                return new SnapshotPageReader(source, buffer, firstPage, length, PageBytes, PhysicalPageBytes);
            }
            catch (IOException exception)
            {
                source?.Dispose();
                throw SnapshotPersistenceException.Wrap(exception);
            }
            catch
            {
                source?.Dispose();
                throw;
            }
        }
    }

    internal void ReleaseBuffer(SnapshotBuffer buffer)
    {
        lock (_gate)
        {
            if (!_ownerReleased && !_faulted && buffer.FirstPage >= 0)
            {
                try
                {
                    // A whole chain joins the free list in O(1). Readers hold the buffer reference,
                    // so no page can be reused while a previously opened prefix reader is alive.
                    WriteLink(Writer, buffer.LastPage, _freePage);
                    _freePage = buffer.FirstPage;
                }
                catch (IOException exception)
                {
                    _faulted = true; _storageFailure = SnapshotPersistenceException.Wrap(exception);
                }
            }
            _buffers--;
            _store.Spool.RefundSnapshot(SnapshotStore.BufferMetadataBytes, metadata: true);
            if (_ownerReleased && _buffers == 0) Retire();
        }
    }

    internal void ReleaseOwner()
    {
        lock (_gate)
        {
            if (_ownerReleased) return;
            _ownerReleased = true;
            try { _writer?.Dispose(); }
            catch { _faulted = true; }
            finally { _writer = null; }
            // Sealed history does not keep a writer FD. Its readers reopen the private file and
            // own independent buffer refs. The spool sweep skips every still-owned container.
            if (_buffers == 0) Retire();
        }
    }

    private void Retire()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Spool.RetireSnapshotContainer(_path, _physicalBytes, _metadataBytes);
    }

    private static long ReadLink(Stream source, long offset)
    {
        Span<byte> header = stackalloc byte[SnapshotStore.PageHeaderBytes];
        source.Position = offset;
        source.ReadExactly(header);
        return BinaryPrimitives.ReadInt64LittleEndian(header);
    }

    private static void WriteLink(Stream target, long offset, long next)
    {
        Span<byte> header = stackalloc byte[SnapshotStore.PageHeaderBytes];
        BinaryPrimitives.WriteInt64LittleEndian(header, next);
        target.Position = offset;
        target.Write(header);
    }
}

/// <summary>A charged logical manifest. Multiple leases/readers share its physical page chain.</summary>
internal sealed class SnapshotBuffer : Stream
{
    private readonly object _gate = new();
    private readonly SnapshotContainer _container;
    private int _references = 1;
    private bool _sealed;
    private string? _hash;
    internal long FirstPage = -1, LastPage = -1, ByteLength;
    internal int LastPageBytes;

    internal SnapshotBuffer(SnapshotContainer container) => _container = container;
    internal string PathForTests => _container.PathForTests;
    internal string Hash { get { lock (_gate) { EnsureLive(); return _hash ?? throw new InvalidOperationException("Snapshot is not sealed."); } } }

    private void EnsureLive() => ObjectDisposedException.ThrowIf(_references == 0, this);

    internal void AddRef()
    {
        lock (_gate) { EnsureLive(); _references = checked(_references + 1); }
    }

    internal void ReleaseRef()
    {
        bool release;
        lock (_gate)
        {
            if (_references == 0) return;
            release = --_references == 0;
        }
        if (release) _container.ReleaseBuffer(this);
    }

    internal void Seal()
    {
        lock (_gate)
        {
            EnsureLive();
            if (_sealed) return;
            using var read = OpenSnapshotRead();
            _hash = Convert.ToHexStringLower(SHA256.HashData(read));
            _sealed = true;
        }
    }

    internal Stream OpenSnapshotRead()
    {
        lock (_gate)
        {
            EnsureLive();
            _references = checked(_references + 1);
            try { return _container.OpenReader(this, FirstPage, ByteLength); }
            catch { ReleaseRef(); throw; }
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            EnsureLive();
            if (_sealed) throw new InvalidOperationException("Snapshot is sealed.");
            _container.Write(this, buffer);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Flush() { lock (_gate) EnsureLive(); }
    protected override void Dispose(bool disposing) { if (disposing) ReleaseRef(); base.Dispose(disposing); }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite { get { lock (_gate) return _references > 0 && !_sealed; } }
    public override long Length { get { lock (_gate) { EnsureLive(); return ByteLength; } } }
    public override long Position { get => Length; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class SnapshotPageReader : Stream
{
    private Stream? _source;
    private SnapshotBuffer? _buffer;
    private readonly long _firstPage, _length, _physicalPageBytes;
    private readonly int _pageBytes;
    private long _page, _pageIndex, _position;

    internal SnapshotPageReader(Stream? source, SnapshotBuffer buffer, long firstPage, long length,
        int pageBytes, long physicalPageBytes)
    {
        _source = source; _buffer = buffer; _firstPage = firstPage; _page = firstPage;
        _length = length; _pageBytes = pageBytes; _physicalPageBytes = physicalPageBytes;
    }

    private void EnsureLive() => ObjectDisposedException.ThrowIf(_buffer is null, this);

    private void LocatePage(long position)
    {
        var index = position / _pageBytes;
        if (index < _pageIndex) { _page = _firstPage; _pageIndex = 0; }
        Span<byte> header = stackalloc byte[SnapshotStore.PageHeaderBytes];
        while (_pageIndex < index)
        {
            _source!.Position = _page;
            _source.ReadExactly(header);
            var next = BinaryPrimitives.ReadInt64LittleEndian(header);
            if (next < 0 || next % _physicalPageBytes != 0 || next > _source.Length - _physicalPageBytes)
                throw new InvalidDataException("Private snapshot page chain is incomplete.");
            _page = next; _pageIndex++;
        }
    }

    public override int Read(Span<byte> destination)
    {
        EnsureLive();
        try
        {
            var total = 0;
            while (!destination.IsEmpty && _position < _length)
            {
                LocatePage(_position);
                var offset = checked((int)(_position % _pageBytes));
                var count = checked((int)Math.Min(Math.Min(destination.Length, _pageBytes - offset), _length - _position));
                _source!.Position = checked(_page + SnapshotStore.PageHeaderBytes + offset);
                var read = _source.Read(destination[..count]);
                if (read == 0) throw new EndOfStreamException("Private snapshot payload is incomplete.");
                total += read; _position += read; destination = destination[read..];
            }
            return total;
        }
        catch (IOException exception) { throw SnapshotPersistenceException.Wrap(exception); }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        EnsureLive();
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0) throw new IOException("Cannot seek before the snapshot.");
        return _position = position;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                try { _source?.Dispose(); }
                finally { _source = null; buffer.ReleaseRef(); }
            }
        }
        base.Dispose(disposing);
    }
    public override bool CanRead => _buffer is not null;
    public override bool CanSeek => _buffer is not null;
    public override bool CanWrite => false;
    public override long Length { get { EnsureLive(); return _length; } }
    public override long Position { get { EnsureLive(); return _position; } set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() => EnsureLive();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

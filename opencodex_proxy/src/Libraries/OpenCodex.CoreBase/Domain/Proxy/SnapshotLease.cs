namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>One independently disposable owner of a complete immutable UTF-8 snapshot.</summary>
public sealed class SnapshotLease : IDisposable
{
    private readonly object _gate = new();
    private SnapshotBuffer? _buffer;

    // Adopts the capture's initial reference. AddRef is explicit for every additional owner.
    internal SnapshotLease(SnapshotBuffer buffer)
    {
        _buffer = buffer;
        Length = buffer.Length;
        Hash = buffer.Hash;
    }

    public long Length { get; }
    public string Hash { get; }
    internal string PathForTests { get { lock (_gate) return Buffer.PathForTests; } }
    private SnapshotBuffer Buffer => _buffer ?? throw new ObjectDisposedException(nameof(SnapshotLease));

    public SnapshotLease AddRef()
    {
        lock (_gate)
        {
            var buffer = Buffer;
            buffer.AddRef();
            try { return new SnapshotLease(buffer); }
            catch { buffer.ReleaseRef(); throw; }
        }
    }

    /// <summary>The returned reader owns a reference even after this lease or a log owner is released.</summary>
    public Stream Read()
    {
        lock (_gate) return Buffer.OpenSnapshotRead();
    }

    /// <summary>Shares exact sealed bytes; committing the log releases only the log's owner.</summary>
    public ProxyLogBodyHandle ToLogHandle()
    {
        lock (_gate)
        {
            _ = Buffer;
            var file = ProxyAuditSpool.AuditFile.FromLease(this);
            try { return ProxyLogBodyHandle.FromSpool(file); }
            catch { file.Dispose(); throw; }
        }
    }

    public void Dispose()
    {
        SnapshotBuffer? buffer;
        lock (_gate) { buffer = _buffer; _buffer = null; }
        buffer?.ReleaseRef();
    }
}

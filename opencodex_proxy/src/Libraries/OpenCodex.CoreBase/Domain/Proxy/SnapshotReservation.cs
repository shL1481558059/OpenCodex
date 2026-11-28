using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.ProtocolConversion.Abstractions;

namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// Request-scoped snapshot ownership. Disposing the owner releases its containers while
/// independently owned sealed leases/readers keep their own physical storage alive.
/// </summary>
public sealed class SnapshotReservation : IDisposable
{
    private readonly object _containersGate = new();
    private readonly SnapshotStore _store;
    private readonly CancellationToken _cancellationToken;
    private readonly IProtocolAuditCapture _auditCapture;
    private SnapshotContainer? _history, _audit;
    internal bool Disposed;

    internal SnapshotReservation(SnapshotStore store, CancellationToken cancellationToken)
    {
        _store = store;
        _cancellationToken = cancellationToken;
        _auditCapture = new ScopedAuditCapture(this);
    }

    public IProtocolAuditCapture AuditCapture => _auditCapture;

    internal void ThrowIfDisposed()
    {
        lock (_store.Spool.QuotaGate) ObjectDisposedException.ThrowIf(Disposed, this);
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private void CheckCancellation(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private SnapshotBuffer CreateBuffer(bool audit, CancellationToken cancellationToken)
    {
        CheckCancellation(cancellationToken);
        lock (_containersGate)
        {
            ThrowIfDisposed();
            var container = audit
                ? (_audit ??= new SnapshotContainer(_store, this))
                : (_history ??= new SnapshotContainer(_store, this));
            return container.CreateBuffer();
        }
    }

    public SnapshotLease CaptureJson(object? sealedValue, CancellationToken cancellationToken = default)
        => CaptureJson(sealedValue, audit: false, cancellationToken);

    private SnapshotLease CaptureJson(object? sealedValue, bool audit, CancellationToken cancellationToken)
    {
        var body = CreateBuffer(audit, cancellationToken);
        try
        {
            using (var writer = new Utf8JsonWriter(body, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                ProxyAuditSpool.WriteJson(writer, sealedValue, 0);
                writer.Flush();
            }
            CheckCancellation(cancellationToken);
            body.Seal();
            return new SnapshotLease(body);
        }
        catch { body.ReleaseRef(); throw; }
    }

    public SnapshotLease CaptureUtf8(ReadOnlySpan<byte> value, CancellationToken cancellationToken = default)
        => CaptureUtf8(value, audit: false, cancellationToken);

    private SnapshotLease CaptureUtf8(ReadOnlySpan<byte> value, bool audit, CancellationToken cancellationToken)
    {
        var body = CreateBuffer(audit, cancellationToken);
        try
        {
            body.Write(value);
            CheckCancellation(cancellationToken);
            body.Seal();
            return new SnapshotLease(body);
        }
        catch { body.ReleaseRef(); throw; }
    }

    public ProxyLogBodyHandle CaptureAuditJson(object? sealedValue, CancellationToken cancellationToken = default)
    {
        using var lease = CaptureJson(sealedValue, audit: true, cancellationToken);
        return lease.ToLogHandle();
    }

    /// <summary>Captures exact UTF-8 audit bytes in the short-lived audit area, never the history area.</summary>
    public ProxyLogBodyHandle CaptureAuditUtf8(ReadOnlySpan<byte> value, CancellationToken cancellationToken = default)
    {
        using var lease = CaptureUtf8(value, audit: true, cancellationToken);
        return lease.ToLogHandle();
    }

    public void Dispose()
    {
        if (!_store.Spool.CompleteReservation(this)) return;
        SnapshotContainer? history, audit;
        lock (_containersGate)
        {
            history = _history; audit = _audit;
            _history = null; _audit = null;
        }
        try { audit?.ReleaseOwner(); }
        finally { history?.ReleaseOwner(); }
    }

    private sealed class ScopedAuditCapture(SnapshotReservation owner) : IProtocolAuditCapture
    {
        // This path always writes to the charged, paged private container; no unbilled RAM prefix.
        public int MemoryThresholdBytes => 0;
        public IProtocolAuditBuffer CreateStaging(CancellationToken cancellationToken)
            => new AuditBuffer(owner.CreateBuffer(audit: true, cancellationToken), owner, cancellationToken);
        public IProtocolAuditBody CaptureJson(object? value, CancellationToken cancellationToken)
            => new AuditBody(owner.CaptureJson(value, audit: true, cancellationToken));
    }

    private sealed class AuditBuffer(SnapshotBuffer inner, SnapshotReservation owner,
        CancellationToken cancellationToken) : IProtocolAuditBuffer
    {
        private SnapshotBuffer? _inner = inner;
        private SnapshotBuffer Inner => _inner ?? throw new ObjectDisposedException(nameof(AuditBuffer));
        public long Length => Inner.Length;
        public Stream OpenSnapshotRead() => Inner.OpenSnapshotRead();
        public void Write(ReadOnlySpan<byte> value)
        {
            owner.CheckCancellation(cancellationToken);
            Inner.Write(value);
        }
        public void Dispose() => Interlocked.Exchange(ref _inner, null)?.ReleaseRef();
    }

    private sealed class AuditBody(SnapshotLease inner) : IProtocolAuditBody
    {
        public long Length => inner.Length;
        public void Dispose() => inner.Dispose();
    }
}

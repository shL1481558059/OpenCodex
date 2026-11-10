using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Core.Domain;

namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// One immutable audit-body version, not an identity cache for mutable dictionaries.
/// Capture explicitly after a request/round rewrite. After commit only its content address remains;
/// a missing persisted manifest is an audit failure, never an invitation to reserialize changed input.
/// </summary>
public sealed class ProxyLogBodyHandle : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly object _gate = new();
    private ReadOnlyMemory<byte>? _utf8;
    private string? _hash;
    private ProxyAuditSpool.AuditFile? _spool;

    private ProxyLogBodyHandle(ReadOnlyMemory<byte> utf8)
    {
        _utf8 = utf8;
        Length = utf8.Length;
    }

    private ProxyLogBodyHandle(ProxyAuditSpool.AuditFile spool)
    { _spool=spool; _hash=spool.Hash; HashComputations=1; Length=checked((int)spool.Length); }
    internal static ProxyLogBodyHandle FromSpool(ProxyAuditSpool.AuditFile spool) => new(spool);

    public Guid Version { get; } = Guid.NewGuid();
    public int Length { get; }

    public static ProxyLogBodyHandle CaptureJson(object? value) =>
        new(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    /// <summary>Copies caller-owned memory, so future mutations cannot alter this version.</summary>
    public static ProxyLogBodyHandle CaptureUtf8(ReadOnlySpan<byte> utf8) => new(utf8.ToArray());

    // Only for buffers exclusively owned by the caller (serializer/request reader), which must
    // remain immutable. The handle never returns them to a pool or exposes writable memory.
    internal static ProxyLogBodyHandle FromOwnedUtf8(ReadOnlyMemory<byte> utf8) => new(utf8);

    internal int HashComputations { get; private set; }

    internal Snapshot ReadSnapshot()
    {
        lock (_gate)
        {
            if (_hash is null)
            {
                _hash = Convert.ToHexStringLower(SHA256.HashData(_utf8!.Value.Span));
                HashComputations++;
            }
            return new Snapshot(_utf8, _hash, Length, _spool);
        }
    }

    internal void ReleaseCommittedBuffer()
    {
        lock (_gate)
        {
            // ReadSnapshot must have computed the address before a committed writer releases bytes.
            if (_hash is null) throw new InvalidOperationException("Body has not been addressed.");
            _utf8 = null;
            _spool?.Dispose(); _spool = null;
        }
    }

    public void Dispose() { lock (_gate) { _spool?.Dispose(); _spool=null; } }

    internal readonly record struct Snapshot(ReadOnlyMemory<byte>? Utf8, string Hash, int Length, ProxyAuditSpool.AuditFile? Spool = null);
}

/// <summary>Request-owned snapshots. Each route attempt/changed round gets a new upstream version.</summary>
public sealed record ProxyLogBodyHandles(
    ProxyLogBodyHandle? Headers,
    ProxyLogBodyHandle? Request,
    ProxyLogBodyHandle? UpstreamRequest = null)
{
    public ProxyLogBodyHandle? ForSlot(RequestLogContentSlot slot) => slot switch
    {
        RequestLogContentSlot.RequestHeaders => Headers,
        RequestLogContentSlot.RequestBody => Request,
        RequestLogContentSlot.UpstreamRequestBody => UpstreamRequest,
        _ => null
    };
}

using System.Security.Cryptography;
using System.Text.Json;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Json;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>Explicit shared storage and connection/tenant isolation, with no lossy TTL/LRU cache.</summary>
public sealed class ClientConnectionCache(SnapshotStore store) : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Connection> _connections = [];
    private bool _disposed;
    public SnapshotStore Store => store;

    /// <summary>Worst committed output for a minimal ordinary WS request, using the same root layout policy.</summary>
    public static long MinimumCommitCredits(SnapshotStore store)
    {
        var limits = ProtocolConversionParameters.Current;
        var hash = new string('0', SHA256.HashSizeInBytes * 2);
        var manifest = new D
        {
            ["id"] = "", ["request_metadata"] = hash, ["input"] = new List<object?>(),
            ["response_metadata"] = hash, ["output"] = new List<object?>()
        };
        var identity = new D { ["id"] = "", ["tenant"] = Guid.Empty.ToString("N"), ["connection"] = Guid.Empty.ToString("N") };
        var references = checked((long)limits.SseMaxCaptureItems
            * (ProtocolJsonWriter.Count(hash, audit: true, CancellationToken.None) + 1));
        var bytes = checked(limits.SseMaxCaptureBytes + 2L * limits.SseMaxEventBytes + references
            + ProtocolJsonWriter.Count(manifest, audit: true, CancellationToken.None)
            + ProtocolJsonWriter.Count(identity, audit: true, CancellationToken.None));
        return store.EstimateCredits(bytes, checked(limits.SseMaxCaptureItems + 3), 2);
    }

    public Connection Open(Guid tenant)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var connection = new Connection(this, tenant);
            _connections.Add(connection);
            return connection;
        }
    }

    internal (int Entries, long Bytes) Usage
    {
        get
        {
            lock (_gate)
            {
                var usage = store.Usage;
                return (_connections.Sum(value => value.Count), checked(usage.PhysicalBytes + usage.MetadataBytes));
            }
        }
    }

    private void Remove(Connection connection) { lock (_gate) _connections.Remove(connection); }
    public void Dispose()
    {
        Connection[] connections;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            connections = _connections.ToArray();
            _connections.Clear();
        }
        foreach (var connection in connections) connection.Dispose();
    }

    public sealed class Connection : IDisposable
    {
        private sealed class Entry(SnapshotLease identity)
        {
            internal readonly SnapshotLease Identity = identity;
            internal ClientHistoryVersion? History;
            internal bool Completed;
        }
        private readonly ClientConnectionCache _owner;
        private readonly Guid _tenant;
        private readonly Guid _scope = Guid.NewGuid();
        private readonly SnapshotLease _scopeIdentity;
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private bool _closed;
        internal Connection(ClientConnectionCache owner, Guid tenant)
        {
            _owner = owner;
            _tenant = tenant;
            var identity = IdentityRecord("");
            using var reservation = Store.Reserve(Store.EstimateCredits(
                ProtocolJsonWriter.Count(identity, audit: true, CancellationToken.None), 1, 1));
            _scopeIdentity = reservation.CaptureJson(identity);
        }
        internal SnapshotStore Store => _owner.Store;
        internal int Count { get { lock (_gate) return _entries.Count; } }
        internal D IdentityRecord(string id) => new()
        {
            ["id"] = id, ["tenant"] = _tenant.ToString("N"), ["connection"] = _scope.ToString("N")
        };

        public bool Knows(string id) { lock (_gate) return !_closed && _entries.ContainsKey(id); }
        public bool IsCompleted(string id)
        {
            lock (_gate) return !_closed && _entries.TryGetValue(id, out var entry) && entry.Completed;
        }
        public ClientHistoryVersion? AcquireHistory(string id)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                return _entries.TryGetValue(id, out var entry) ? entry.History?.AddRef() : null;
            }
        }

        /// <summary>Terminal bookkeeping for an already registered identity; performs no storage IO.</summary>
        public void MarkCompleted(string id)
        {
            lock (_gate)
            {
                if (_closed || !_entries.TryGetValue(id, out var entry)) return;
                entry.Completed = true;
            }
        }

        public void DiscardHistory(string id)
        {
            ClientHistoryVersion? version;
            lock (_gate)
            {
                if (_closed || !_entries.TryGetValue(id, out var entry)) return;
                version = entry.History;
                entry.History = null;
                entry.Completed = false;
            }
            version?.Dispose();
        }

        public SnapshotReservation ReserveIdentity(CancellationToken cancellationToken)
        {
            var bytes = checked(ProtocolConversionParameters.Current.SseMaxEventBytes
                + ProtocolJsonWriter.Count(IdentityRecord(""), audit: true, cancellationToken));
            return Store.Reserve(Store.EstimateCredits(bytes, 1, 1), cancellationToken);
        }
        public void RememberId(string id, SnapshotReservation reservation, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_entries.ContainsKey(id)) return;
                var identity = reservation.CaptureJson(IdentityRecord(id), cancellationToken);
                try { _entries.Add(id, new(identity)); }
                catch { identity.Dispose(); throw; }
            }
        }
        public void RememberCompletion(string id, SnapshotReservation reservation, CancellationToken cancellationToken = default)
        {
            RememberId(id, reservation, cancellationToken);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _entries[id].Completed = true;
            }
        }
        internal void Commit(string id, ClientHistoryVersion version, SnapshotReservation reservation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RememberId(id, reservation, cancellationToken);
            ClientHistoryVersion? previous;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                var entry = _entries[id];
                previous = entry.History;
                entry.History = version;
                entry.Completed = true;
            }
            previous?.Dispose();
        }

        public PreparedClientRequest Prepare(D request, ClientHistoryVersion? previous, bool generate,
            CancellationToken cancellationToken)
        {
            using var pinned = previous?.AddRef();
            previous = pinned;
            var current = ReadInput(request);
            var history = previous is null ? new List<object?>()
                : previous.Input.Concat(previous.Output).Select(ClientHistoryVersion.ReadValue).ToList();
            var isFullHistory = previous is not null && IsPrefix(history, current);
            var full = previous?.ReadRequestMetadata() ?? new D(StringComparer.Ordinal);
            foreach (var pair in request) full[pair.Key] = pair.Value;
            full["input"] = previous is null || isFullHistory ? current : history.Concat(current).ToList();
            full.Remove("previous_response_id");
            full.Remove("type");
            full.Remove("generate");
            full.Remove("stream_id");
            if (generate) full["stream"] = true;
            OpenCodex.Core.ProtocolConversion.ProtocolConversionErrors.Wrap(
                () => ProtocolJsonWriter.ValidateRequest(full, cancellationToken));

            var metadata = new D(full, StringComparer.Ordinal);
            metadata.Remove("input");
            var shared = previous is null ? Array.Empty<SnapshotLease>() : previous.Input.Concat(previous.Output).ToArray();
            var additions = previous is null || !isFullHistory ? current : current.Skip(shared.Length).ToList();
            var shareMetadata = previous is not null && JsonValueComparer.AreEqual(previous.ReadRequestMetadata(), metadata);
            var bytes = shareMetadata ? 0L : ProtocolJsonWriter.Count(metadata, audit: true, cancellationToken);
            foreach (var item in additions) bytes = checked(bytes + ProtocolJsonWriter.Count(item, audit: true, cancellationToken));
            var buffers = checked(additions.Count + (shareMetadata ? 0 : 1));
            if (generate)
            {
                var hash = new string('0', SHA256.HashSizeInBytes * 2);
                var manifest = new D
                {
                    ["id"] = "", ["request_metadata"] = hash, ["input"] = new List<object?>(),
                    ["response_metadata"] = hash, ["output"] = new List<object?>()
                };
                var referenceBytes = checked(ProtocolJsonWriter.Count(hash, audit: true, cancellationToken) + 1);
                var references = checked(shared.Length + additions.Count + ProtocolConversionParameters.Current.SseMaxCaptureItems);
                bytes = checked(bytes + ProtocolConversionParameters.Current.SseMaxCaptureBytes
                    + 2L * ProtocolConversionParameters.Current.SseMaxEventBytes
                    + references * referenceBytes
                    + ProtocolJsonWriter.Count(manifest, audit: true, cancellationToken)
                    + ProtocolJsonWriter.Count(IdentityRecord(""), audit: true, cancellationToken));
                buffers = checked(buffers + ProtocolConversionParameters.Current.SseMaxCaptureItems + 3);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Cancellation stops execution, not the reserved failure-audit ownership. Each stage still takes its token.
            var reservation = Store.Reserve(Store.EstimateCredits(bytes, buffers, generate ? 2 : 1), CancellationToken.None);
            var owned = new List<SnapshotLease>();
            SnapshotLease? metadataLease = null;
            try
            {
                metadataLease = shareMetadata ? previous!.RequestMetadata.AddRef() : reservation.CaptureJson(metadata, cancellationToken);
                foreach (var value in shared) owned.Add(value.AddRef());
                foreach (var value in additions) owned.Add(reservation.CaptureJson(value, cancellationToken));
                return new(this, full, reservation, metadataLease, owned.ToArray(), generate);
            }
            catch
            {
                metadataLease?.Dispose();
                foreach (var value in owned) value.Dispose();
                reservation.Dispose();
                throw;
            }
        }

        private static List<object?> ReadInput(D request)
        {
            var value = JsonDictionaryValue.Get(request, "input");
            if (value is null) return [];
            if (value is string text)
                return [new D { ["type"] = "message", ["role"] = "user", ["content"] = text }];
            if (value is JsonElement element)
            {
                if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
                if (element.ValueKind == JsonValueKind.String)
                    return [new D { ["type"] = "message", ["role"] = "user", ["content"] = element.GetString() }];
                if (element.ValueKind != JsonValueKind.Array)
                    throw new OpenCodex.Core.Errors.BadRequestException("input must be a string or an array of input items.");
            }
            else if (value is not IEnumerable<object?>)
                throw new OpenCodex.Core.Errors.BadRequestException("input must be a string or an array of input items.");
            return JsonDictionaryValue.List(request, "input");
        }
        private static bool IsPrefix(IReadOnlyList<object?> prefix, IReadOnlyList<object?> values)
        {
            if (values.Count < prefix.Count) return false;
            for (var index = 0; index < prefix.Count; index++)
                if (!JsonValueComparer.AreEqual(prefix[index], values[index])) return false;
            return true;
        }
        public void Dispose()
        {
            Entry[] entries;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                entries = _entries.Values.ToArray();
                _entries.Clear();
            }
            try
            {
                foreach (var entry in entries) { entry.History?.Dispose(); entry.Identity.Dispose(); }
                _scopeIdentity.Dispose();
            }
            finally { _owner.Remove(this); }
        }
    }
}

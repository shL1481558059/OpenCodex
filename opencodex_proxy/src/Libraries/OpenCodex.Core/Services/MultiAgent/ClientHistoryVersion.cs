using System.Security.Cryptography;
using System.Text.Json;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Json;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>One immutable, connection-owned version. Explicit lineage shares sealed item leases.</summary>
public sealed class ClientHistoryVersion : IDisposable
{
    private readonly SnapshotLease _requestMetadata;
    private readonly SnapshotLease _responseMetadata;
    private readonly SnapshotLease _manifest;
    private readonly SnapshotLease[] _input;
    private readonly SnapshotLease[] _output;
    private readonly object _gate = new();
    private bool _disposed;

    internal ClientHistoryVersion(SnapshotLease requestMetadata, SnapshotLease[] input,
        SnapshotLease responseMetadata, SnapshotLease[] output, SnapshotLease manifest)
    {
        _requestMetadata = requestMetadata;
        _input = input;
        _responseMetadata = responseMetadata;
        _output = output;
        _manifest = manifest;
    }

    public int InputCount => _input.Length;
    public int OutputCount => _output.Length;
    internal SnapshotLease RequestMetadata => _requestMetadata;
    internal IReadOnlyList<SnapshotLease> Input => _input;
    internal IReadOnlyList<SnapshotLease> Output => _output;

    public ClientHistoryVersion AddRef()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var acquired = new List<SnapshotLease>();
            try
            {
                var request = _requestMetadata.AddRef();
                acquired.Add(request);
                var input = new SnapshotLease[_input.Length];
                for (var index = 0; index < input.Length; index++)
                {
                    input[index] = _input[index].AddRef();
                    acquired.Add(input[index]);
                }
                var response = _responseMetadata.AddRef();
                acquired.Add(response);
                var output = new SnapshotLease[_output.Length];
                for (var index = 0; index < output.Length; index++)
                {
                    output[index] = _output[index].AddRef();
                    acquired.Add(output[index]);
                }
                var manifest = _manifest.AddRef();
                acquired.Add(manifest);
                var clone = new ClientHistoryVersion(request, input, response, output, manifest);
                acquired.Clear();
                return clone;
            }
            finally { foreach (var lease in acquired) lease.Dispose(); }
        }
    }

    internal D ReadRequestMetadata() => (D)ReadValue(_requestMetadata)!;
    public D MaterializeRequest()
    {
        using var pinned = AddRef();
        var result = pinned.ReadRequestMetadata();
        result["input"] = pinned._input.Select(ReadValue).ToList();
        return result;
    }

    public D MaterializeResponse()
    {
        using var pinned = AddRef();
        var result = (D)ReadValue(pinned._responseMetadata)!;
        result["output"] = pinned._output.Select(ReadValue).ToList();
        return result;
    }

    internal static object? ReadValue(SnapshotLease lease)
    {
        using var source = lease.Read();
        using var document = JsonDocument.Parse(source);
        return WebSearchPayload.FromJsonElement(document.RootElement);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _requestMetadata.Dispose();
        _responseMetadata.Dispose();
        _manifest.Dispose();
        foreach (var value in _input) value.Dispose();
        foreach (var value in _output) value.Dispose();
    }
}

/// <summary>Prepared model input and credits. A successful commit adds independent sealed owners.</summary>
public sealed class PreparedClientRequest : IDisposable
{
    private readonly ClientConnectionCache.Connection _connection;
    private readonly SnapshotLease _metadata;
    private readonly SnapshotLease[] _input;
    private readonly bool _generates;
    private readonly object _gate = new();
    private bool _disposed;
    private bool _committed;

    internal PreparedClientRequest(ClientConnectionCache.Connection connection, D request,
        SnapshotReservation reservation, SnapshotLease metadata, SnapshotLease[] input, bool generates)
    {
        _connection = connection;
        _generates = generates;
        Request = request;
        Reservation = reservation;
        _metadata = metadata;
        _input = input;
    }

    public D Request { get; }
    public SnapshotReservation Reservation { get; }
    public bool Committed { get { lock (_gate) return _committed; } }

    /// <summary>Prewarm has a known response and no model execution; reserve it before publishing any event.</summary>
    public void ReserveKnownResponse(D response, CancellationToken cancellationToken)
    {
        lock (_gate) ReserveKnownResponseCore(response, cancellationToken);
    }

    private void ReserveKnownResponseCore(D response, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_generates) throw new InvalidOperationException("Model execution credits must be reserved before execution.");
        var id = JsonDictionaryValue.String(response, "id");
        var output = JsonDictionaryValue.List(response, "output");
        var metadata = new D(response, StringComparer.Ordinal);
        metadata.Remove("output");
        var hash = new string('0', SHA256.HashSizeInBytes * 2);
        var manifest = new D
        {
            ["id"] = id, ["request_metadata"] = _metadata.Hash,
            ["input"] = _input.Select(value => (object?)value.Hash).ToList(),
            ["response_metadata"] = hash,
            ["output"] = output.Select(_ => (object?)hash).ToList()
        };
        var bytes = checked(ProtocolJsonWriter.Count(metadata, audit: true, cancellationToken)
            + ProtocolJsonWriter.Count(manifest, audit: true, cancellationToken)
            + ProtocolJsonWriter.Count(_connection.IdentityRecord(id), audit: true, cancellationToken));
        foreach (var item in output) bytes = checked(bytes + ProtocolJsonWriter.Count(item, audit: true, cancellationToken));
        Reservation.EnsureRemaining(_connection.Store.EstimateCredits(bytes, output.Count + 3, 1), cancellationToken);
    }

    public void Commit(D response, CancellationToken cancellationToken)
    {
        lock (_gate) CommitCore(response, cancellationToken);
    }

    private void CommitCore(D response, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed) throw new InvalidOperationException("The response history was already committed.");
        if (ProtocolJsonWriter.Count(response, audit: true, cancellationToken) > ProtocolConversionParameters.Current.SseMaxCaptureBytes)
            throw new OpenCodex.Core.Errors.UpstreamException("The history response exceeds MaxCaptureBytes.", errorCode: "output_capture_limit");
        var id = JsonDictionaryValue.String(response, "id");
        if (id.Length == 0) throw new InvalidOperationException("A history response requires an id.");
        var responseMetadata = new D(response, StringComparer.Ordinal);
        responseMetadata.Remove("output");
        var output = JsonDictionaryValue.List(response, "output");
        var owned = new List<SnapshotLease>();
        ClientHistoryVersion? version = null;
        try
        {
            var metadata = Reservation.CaptureJson(responseMetadata, cancellationToken);
            owned.Add(metadata);
            foreach (var item in output) owned.Add(Reservation.CaptureJson(item, cancellationToken));
            var outputLeases = owned.Skip(1).ToArray();
            var manifest = Reservation.CaptureJson(new D
            {
                ["id"] = id,
                ["request_metadata"] = _metadata.Hash,
                ["input"] = _input.Select(value => (object?)value.Hash).ToList(),
                ["response_metadata"] = metadata.Hash,
                ["output"] = outputLeases.Select(value => (object?)value.Hash).ToList()
            }, cancellationToken);
            owned.Add(manifest);
            var requestMetadata = _metadata.AddRef();
            owned.Add(requestMetadata);
            var inputLeases = new SnapshotLease[_input.Length];
            for (var index = 0; index < inputLeases.Length; index++)
            {
                inputLeases[index] = _input[index].AddRef();
                owned.Add(inputLeases[index]);
            }
            version = new(requestMetadata, inputLeases, metadata, outputLeases, manifest);
            owned.Clear();
            _connection.Commit(id, version, Reservation, cancellationToken);
            version = null;
            _committed = true;
        }
        finally
        {
            version?.Dispose();
            foreach (var value in owned) value.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try
        {
            _metadata.Dispose();
            foreach (var value in _input) value.Dispose();
        }
        finally { Reservation.Dispose(); }
    }
}

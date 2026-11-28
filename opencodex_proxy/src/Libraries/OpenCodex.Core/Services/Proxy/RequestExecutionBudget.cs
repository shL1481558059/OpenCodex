using OpenCodex.Core.Errors;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Abstractions;
using OpenCodex.ProtocolConversion.Json;
using OpenCodex.ProtocolConversion.Streaming;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// Owns the request-scoped snapshot lifetime and audit handles. A WS request may borrow the
/// connection history's reservation until commit; actual file allocation remains bounded by
/// the protocol capture budgets.
/// </summary>
public sealed class RequestExecutionBudget : IDisposable
{
    private readonly SnapshotStore _store;
    private readonly bool _ownsReservation;
    private readonly List<ProxyLogBodyHandle> _handles = [];
    private readonly List<IDisposable> _owners = [];
    private readonly IProtocolAuditCapture _auditCapture;

    private RequestExecutionBudget(ProxyAuditSpool spool, SnapshotReservation? reservation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spool);
        _store = new SnapshotStore(spool);
        _ownsReservation = reservation is null;
        Reservation = reservation ?? _store.Reserve(cancellationToken);
        _auditCapture = new ProtectedAuditCapture(this);
    }

    public SnapshotReservation Reservation { get; }
    public bool OwnsReservation => _ownsReservation;
    public IProtocolAuditCapture AuditCapture => _auditCapture;

    public static RequestExecutionBudget Create(ProxyAuditSpool spool, ProxyRequestMetadata metadata,
        SnapshotReservation? reservation, CancellationToken cancellationToken)
    {
        return new RequestExecutionBudget(spool, reservation ?? metadata.SnapshotReservation, cancellationToken);
    }

    public static RequestExecutionBudget Create(ProxyAuditSpool spool, CancellationToken cancellationToken = default) =>
        new(spool, null, cancellationToken);

    public ProxyRequestMetadata Attach(ProxyRequestMetadata metadata) =>
        metadata.WithSnapshotReservation(Reservation);

    public void EnsureAudit(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reservation.ThrowIfDisposed();
    }

    public void EnsureQueued(ProxyRequestMetadata metadata, object? clientPayload, CancellationToken cancellationToken)
    {
        if (metadata.RawBody is { } rawBody && rawBody.Length > ProtocolConversionParameters.Current.MaxRequestBodyBytes)
            throw new StorageCapacityException("request body too large", 413, "request_too_large");
        EnsureAudit(cancellationToken);
    }

    public void EnsureCall(ProxyRequestMetadata metadata, object? logicalRequest, object? upstreamRequest,
        SseFrameOptions frames, string sourceProtocol, bool isStream, CancellationToken cancellationToken,
        object? knownAuditDetails = null, bool hasBuiltinTools = false)
    {
        ValidateRequest(logicalRequest, cancellationToken);
        ValidateRequest(upstreamRequest, cancellationToken);
        if (metadata.RawBody is { } raw && raw.Length > ProtocolConversionParameters.Current.MaxRequestBodyBytes)
            throw new StorageCapacityException("request body too large", 413, "request_too_large");

        EnsureAuditPayload(cancellationToken);
    }

    private void EnsureAuditPayload(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reservation.ThrowIfDisposed();
        }
        catch (Exception exception) when (ResourceErrors.IsResourceFailure(exception))
        {
            throw ResourceErrors.Normalize(exception);
        }
    }

    /// <summary>Called immediately before an actual tool invocation, not after an unreserved result.</summary>
    public void EnsureTool(object? knownDetails, object? sourceRequest, SseFrameOptions frames,
        string sourceProtocol, bool isStream, CancellationToken cancellationToken)
    {
        ValidateRequest(sourceRequest, cancellationToken);
        EnsureAudit(cancellationToken);
    }

    public static void ValidateToolResult(WebSearchToolResult result, CancellationToken cancellationToken)
    {
        if (!IsBoundedToolRaw(result.Raw, cancellationToken))
            throw new StorageCapacityException("Tool-provider source exceeds its declared capture budget; no wire evidence was fabricated.",
                507, "output_capture_limit");
        ValidateRequest(new Dictionary<string, object?>
        {
            ["call_id"] = result.CallId, ["item_id"] = result.ItemId, ["query"] = result.Query,
            ["status"] = result.Status, ["tool_result"] = result.ToolResult, ["result"] = result.OpenCodexResult,
            ["log_error"] = result.LogError, ["provider"] = result.Provider, ["key_id"] = result.KeyId,
            ["key_position"] = result.KeyPosition, ["key_usage_count"] = result.KeyUsageCount,
            ["key_usage_limit"] = result.KeyUsageLimit, ["error_type"] = result.ErrorType,
            ["http_status"] = result.HttpStatus, ["raw"] = result.Raw, ["disable_search"] = result.DisableSearch
        }, cancellationToken);
    }

    internal static bool IsBoundedToolRaw(object? raw, CancellationToken cancellationToken = default)
    {
        if (raw is null) return true;
        var parameters = ProtocolConversionParameters.Current;
        var maximum = UpstreamJsonEvidence.IsEvidence(raw) || ReceivedFrameEvidence.IsEvidence(raw)
            ? UpstreamJsonEvidence.MaxJsonBytes(parameters.SseMaxCaptureBytes, parameters.FlushBytes)
            : parameters.SseMaxCaptureBytes;
        return AuditBytes(raw, cancellationToken) <= maximum;
    }

    internal static string ToolRawRepresentation(object? raw)
    {
        if (raw is null) return "not_provided";
        if (UpstreamJsonEvidence.IsEvidence(raw) || ReceivedFrameEvidence.IsEvidence(raw)) return "received_json_evidence";
        if (raw is IReadOnlyDictionary<string, object?> value
            && value.TryGetValue("_opencodex_capture", out var metadata)
            && metadata is IReadOnlyDictionary<string, object?> capture
            && capture.TryGetValue("representation", out var representation)
            && representation is "parsed_value_without_wire") return "parsed_value_without_wire";
        return "provider_parsed_result";
    }

    // Responses can use upstream WS: explicitly reserve its existing receive budget M.
    // Chat/Messages are HTTP SSE and use E. The downstream writer keeps its own E policy.
    public static int AcceptedSourceFrameBytes(string sourceProtocol) => sourceProtocol == ProtocolConverter.Responses
        ? ProtocolConversionParameters.Current.SseMaxWebSocketMessageBytes
        : ProtocolConversionParameters.Current.SseMaxEventBytes;

    public static void ValidateRequest(object? request, CancellationToken cancellationToken)
    {
        try { ProtocolJsonWriter.ValidateRequest(request, cancellationToken); }
        catch (Exception exception) when (ResourceErrors.IsResourceFailure(exception))
        {
            throw ResourceErrors.Normalize(exception);
        }
    }

    public static long AuditBytes(object? value, CancellationToken cancellationToken) =>
        ProtocolJsonWriter.Count(value, audit: true, cancellationToken);

    public static void ValidateCompleteJson(object? value, string slot, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (value is null) return;
        if (AuditBytes(value, cancellationToken) > maximumBytes)
            throw new StorageCapacityException($"Complete {slot} audit exceeds its byte budget; no content was truncated.",
                507, "response_capture_limit_exceeded");
    }

    public ProxyLogBodyHandle CaptureAuditJson(object? value, CancellationToken cancellationToken)
    {
        CheckAvailable(AuditBytes(value, cancellationToken), cancellationToken);
        try
        {
            var handle = Reservation.CaptureAuditJson(value, cancellationToken);
            _handles.Add(handle);
            return handle;
        }
        catch (Exception exception) when (ResourceErrors.IsResourceFailure(exception))
        {
            throw ResourceErrors.Normalize(exception);
        }
    }

    public ProxyLogBodyHandle CaptureAuditUtf8(ReadOnlySpan<byte> value, CancellationToken cancellationToken)
    {
        CheckAvailable(value.Length, cancellationToken);
        try
        {
            var handle = Reservation.CaptureAuditUtf8(value, cancellationToken);
            _handles.Add(handle);
            return handle;
        }
        catch (Exception exception) when (ResourceErrors.IsResourceFailure(exception))
        {
            throw ResourceErrors.Normalize(exception);
        }
    }

    /// <summary>Transfers a returned audit handle to the log pipeline; this budget no longer disposes it.</summary>
    public ProxyLogBodyHandle? TransferToLog(ProxyLogBodyHandle? handle)
    {
        if (handle is not null) _handles.Remove(handle);
        return handle;
    }

    public ProxyLogBodyHandles TransferLogBodies(ProxyLogBodyHandles bodies)
    {
        TransferToLog(bodies.Headers);
        TransferToLog(bodies.Request);
        TransferToLog(bodies.UpstreamRequest);
        return bodies;
    }

    private void CheckAvailable(long physicalPayloadBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reservation.ThrowIfDisposed();
    }

    public T Retain<T>(T owner) where T : IDisposable
    {
        _owners.Add(owner);
        return owner;
    }

    public void Dispose()
    {
        try
        {
            foreach (var handle in _handles) handle.Dispose();
            foreach (var owner in _owners) owner.Dispose();
        }
        finally { if (_ownsReservation) Reservation.Dispose(); }
    }

    private sealed class ProtectedAuditCapture(RequestExecutionBudget budget) : IProtocolAuditCapture
    {
        public int MemoryThresholdBytes => 0;
        public IProtocolAuditBuffer CreateStaging(CancellationToken cancellationToken)
        {
            budget.CheckAvailable(0, cancellationToken);
            var buffer = budget.Reservation.AuditCapture.CreateStaging(cancellationToken);
            return new ProtectedAuditBuffer(buffer, budget, cancellationToken);
        }
        public IProtocolAuditBody CaptureJson(object? value, CancellationToken cancellationToken)
        {
            budget.CheckAvailable(AuditBytes(value, cancellationToken), cancellationToken);
            return budget.Reservation.AuditCapture.CaptureJson(value, cancellationToken);
        }
    }

    private sealed class ProtectedAuditBuffer(IProtocolAuditBuffer inner, RequestExecutionBudget budget,
        CancellationToken cancellationToken) : IProtocolAuditBuffer
    {
        public long Length => inner.Length;
        public Stream OpenSnapshotRead() => inner.OpenSnapshotRead();
        public void Write(ReadOnlySpan<byte> value)
        {
            budget.CheckAvailable(value.Length, cancellationToken);
            inner.Write(value);
        }
        public void Dispose() => inner.Dispose();
    }
}

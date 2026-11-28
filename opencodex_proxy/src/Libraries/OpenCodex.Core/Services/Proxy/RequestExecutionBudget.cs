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
/// A ledger adapter, not another storage backend. A WS reservation is borrowed until the
/// history owner commits; a direct Core/HTTP request owns a normal reservation on the injected spool.
/// Every audit write is fenced from the history owner's unused commit credits.
/// </summary>
public sealed class RequestExecutionBudget : IDisposable
{
    private readonly SnapshotStore _store;
    private readonly bool _ownsReservation;
    private readonly List<ProxyLogBodyHandle> _handles = [];
    private readonly List<IDisposable> _owners = [];
    private readonly IProtocolAuditCapture _auditCapture;
    private bool _auditAreaStarted;

    private RequestExecutionBudget(ProxyAuditSpool spool, SnapshotReservation? reservation,
        long historyCommitCredits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentOutOfRangeException.ThrowIfNegative(historyCommitCredits);
        if (reservation is null && historyCommitCredits != 0)
            throw new ArgumentException("History commit credits need their explicit reservation.", nameof(reservation));
        _store = new SnapshotStore(spool);
        _ownsReservation = reservation is null;
        Reservation = reservation ?? _store.Reserve(_store.EstimateCredits(0, 0, 0), cancellationToken);
        HistoryCommitCredits = historyCommitCredits;
        _auditCapture = new ProtectedAuditCapture(this);
    }

    public SnapshotReservation Reservation { get; }
    public bool OwnsReservation => _ownsReservation;
    public long HistoryCommitCredits { get; }
    public IProtocolAuditCapture AuditCapture => _auditCapture;

    public static RequestExecutionBudget Create(ProxyAuditSpool spool, ProxyRequestMetadata metadata,
        SnapshotReservation? reservation, long historyCommitCredits, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(historyCommitCredits);
        ArgumentOutOfRangeException.ThrowIfNegative(metadata.HistoryCommitCredits);
        if (reservation is not null && metadata.SnapshotReservation is not null
            && !ReferenceEquals(reservation, metadata.SnapshotReservation))
            throw new ArgumentException("Request metadata and context must share one snapshot reservation.");
        return new RequestExecutionBudget(spool, reservation ?? metadata.SnapshotReservation,
            Math.Max(historyCommitCredits, metadata.HistoryCommitCredits), cancellationToken);
    }

    public static RequestExecutionBudget Create(ProxyAuditSpool spool, CancellationToken cancellationToken = default) =>
        new(spool, null, 0, cancellationToken);

    public ProxyRequestMetadata Attach(ProxyRequestMetadata metadata) =>
        metadata.WithSnapshotReservation(Reservation, HistoryCommitCredits);

    public void EnsureAudit(long physicalPayloadBytes, int maxLiveBuffers, CancellationToken cancellationToken)
    {
        try
        {
            Reservation.EnsureRemaining(checked(HistoryCommitCredits
                + _store.EstimateCredits(physicalPayloadBytes, maxLiveBuffers, 2)), cancellationToken);
        }
        catch (Exception exception) when (ResourceErrors.IsResourceFailure(exception))
        {
            throw ResourceErrors.Normalize(exception);
        }
    }

    public void EnsureQueued(ProxyRequestMetadata metadata, object? clientPayload, CancellationToken cancellationToken)
    {
        if (metadata.RawBody is { } rawBody && rawBody.Length > ProtocolConversionParameters.Current.MaxRequestBodyBytes)
            throw new StorageCapacityException("request body too large", 413, "request_too_large");
        var bodies = metadata.LogBodies;
        var headers = bodies?.Headers is null ? AuditBytes(metadata.Headers, cancellationToken) : 0;
        var request = bodies?.Request is null
            ? metadata.RawBody is { Length: > 0 } raw ? raw.Length : AuditBytes(clientPayload, cancellationToken)
            : 0;
        EnsureAudit(checked(headers + request), 2, cancellationToken);
    }

    public void EnsureCall(ProxyRequestMetadata metadata, object? logicalRequest, object? upstreamRequest,
        SseFrameOptions frames, string sourceProtocol, bool isStream, CancellationToken cancellationToken,
        object? knownAuditDetails = null, bool hasBuiltinTools = false)
    {
        ValidateRequest(logicalRequest, cancellationToken);
        ValidateRequest(upstreamRequest, cancellationToken);
        if (metadata.RawBody is { } raw && raw.Length > ProtocolConversionParameters.Current.MaxRequestBodyBytes)
            throw new StorageCapacityException("request body too large", 413, "request_too_large");

        var requestBytes = AuditBytes(upstreamRequest, cancellationToken);
        var missingHeaders = metadata.LogBodies?.Headers is null ? AuditBytes(metadata.Headers, cancellationToken) : 0;
        var missingRequest = metadata.LogBodies?.Request is null
            ? metadata.RawBody is { Length: > 0 } body ? body.Length : AuditBytes(logicalRequest, cancellationToken)
            : 0;
        var knownDetailsBytes = knownAuditDetails is null ? 0 : AuditBytes(knownAuditDetails, cancellationToken);
        EnsureAuditPayload(requestBytes, missingHeaders, missingRequest, frames, sourceProtocol, isStream,
            cancellationToken, knownDetailsBytes, hasBuiltinTools);
    }

    private void EnsureAuditPayload(long requestBytes, long missingHeaders, long missingRequest, SseFrameOptions frames,
        string sourceProtocol, bool isStream, CancellationToken cancellationToken, long knownDetailsBytes, bool hasBuiltinTools)
    {
        try
        {
            Reservation.EnsureRemaining(checked(HistoryCommitCredits + EstimateCallCredits(_store, requestBytes,
                missingHeaders, missingRequest, frames, sourceProtocol, isStream, knownDetailsBytes, hasBuiltinTools)),
                cancellationToken);
        }
        catch (Exception exception) when (ResourceErrors.IsResourceFailure(exception))
        {
            throw ResourceErrors.Normalize(exception);
        }
    }

    private static long EstimateCallCredits(SnapshotStore store, long requestBytes, long missingHeaders,
        long missingRequest, SseFrameOptions frames, string sourceProtocol, bool isStream,
        long knownDetailsBytes = 0, bool hasBuiltinTools = false)
    {
        var acceptedSourceFrameBytes = isStream ? AcceptedSourceFrameBytes(sourceProtocol) : 0;
        // Text/binary share C; raw UTF-16 is 2C. Deferred default-encoder
        // serialization can temporarily write six bytes per source byte before Spend.
        var rawBytes = isStream ? checked(2L * frames.MaxCaptureBytes + 6L * acceptedSourceFrameBytes + 2) : 0;
        var receivedJournalBytes = isStream
            ? ReceivedFrameEvidence.MaxJournalBytes(frames.MaxCaptureBytes, acceptedSourceFrameBytes) : 0;
        var rawBuffers = isStream ? checked(2 * frames.MaxCaptureFields) : 0;
        // A is an explicit failure-evidence bound, not a larger normal C policy.
        var httpErrorAuditBytes = UpstreamJsonEvidence.MaxJsonBytes(ProtocolConversionParameters.Current.SseMaxCaptureBytes,
            ProtocolConversionParameters.Current.FlushBytes);
        // A model stream may fail at HTTP headers/body before producing any SSE/WS
        // frames. That producer uses root C/F, independently of this capture's C.
        // Reserve both source representations explicitly; neither changes a wire limit.
        var sourceAuditBytes = isStream
            ? checked(ReceivedFrameEvidence.MaxJsonBytes(frames.MaxCaptureBytes, acceptedSourceFrameBytes) + httpErrorAuditBytes)
            : httpErrorAuditBytes;
        var completionBytes = checked(sourceAuditBytes + 2L * frames.MaxCaptureBytes);
        // Existing tool details include whole P-sized requests; measure them exactly.
        // One new round retains this actual source request. The two C slots only
        // cover new bounded model usage/tool-call summary and other completion metadata.
        var detailBytes = checked(knownDetailsBytes + (hasBuiltinTools ? requestBytes : 0)
            + 2L * frames.MaxCaptureBytes);
        return store.EstimateCredits(checked(requestBytes + missingHeaders + missingRequest + rawBytes
            + receivedJournalBytes + completionBytes + detailBytes),
            checked(rawBuffers + (isStream ? 1 : 0) + 3 + 3 + 2), 2);
    }

    /// <summary>Called immediately before an actual tool invocation, not after an unreserved result.</summary>
    public void EnsureTool(object? knownDetails, object? sourceRequest, SseFrameOptions frames,
        string sourceProtocol, bool isStream, CancellationToken cancellationToken)
    {
        ValidateRequest(sourceRequest, cancellationToken);
        var detailsBytes = knownDetails is null ? 0 : AuditBytes(knownDetails, cancellationToken);
        var sourceBytes = AuditBytes(sourceRequest, cancellationToken);
        var parameters = ProtocolConversionParameters.Current;
        var toolFailureBytes = UpstreamJsonEvidence.MaxJsonBytes(parameters.SseMaxCaptureBytes, parameters.FlushBytes);
        var modelSourceBytes = isStream
            ? ReceivedFrameEvidence.MaxJsonBytes(frames.MaxCaptureBytes, AcceptedSourceFrameBytes(sourceProtocol))
            : toolFailureBytes;
        // Normal complete tool data must fit P; a genuine producer failure retains
        // its independently bounded C+Flush evidence A. Neither changes P or C policy.
        EnsureAudit(checked(detailsBytes + sourceBytes + parameters.MaxRequestBodyBytes + toolFailureBytes
            + modelSourceBytes + 2L * frames.MaxCaptureBytes), 8, cancellationToken);
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

    /// <summary>
    /// Startup-only lower admission requirement for a fixed minimal request and worst
    /// legal output. No P-sized input is assumed, no quota is allocated, and no root
    /// value is increased. The host may add its separate WS history-commit requirement.
    /// </summary>
    public static long MinimumExecutionCredits(SnapshotStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var frames = new SseFrameOptions();
        var minimal = new Dictionary<string, object?>
        {
            ["model"] = "m", ["input"] = Array.Empty<object?>(), ["stream"] = true
        };
        var requestBytes = AuditBytes(minimal, CancellationToken.None);
        var headersBytes = AuditBytes(new Dictionary<string, string>(), CancellationToken.None);
        var queued = store.EstimateCredits(checked(requestBytes + headersBytes), 2, 2);
        // Compare independent phase reservations; never alter any protocol wire limit.
        var responses = EstimateCallCredits(store, requestBytes, 0, 0, frames, ProtocolConverter.Responses, true);
        var httpSse = EstimateCallCredits(store, requestBytes, 0, 0, frames, ProtocolConverter.Chat, true);
        var httpJson = EstimateCallCredits(store, requestBytes, 0, 0, frames, ProtocolConverter.Responses, false);
        return checked(queued + Math.Max(responses, Math.Max(httpSse, httpJson)));
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
            _auditAreaStarted = true;
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
            _auditAreaStarted = true;
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
        var captureCredits = _store.EstimateCredits(physicalPayloadBytes, 1, 2);
        if (_auditAreaStarted)
            captureCredits = checked(captureCredits - _store.EstimateCredits(0, 0, 2));
        var required = checked(HistoryCommitCredits + captureCredits);
        var available = Reservation.RemainingBytes;
        if (available < required)
            throw ResourceErrors.Normalize(new SnapshotCapacityExceededException(required, available));
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
            budget._auditAreaStarted = true;
            return new ProtectedAuditBuffer(buffer, budget, cancellationToken);
        }
        public IProtocolAuditBody CaptureJson(object? value, CancellationToken cancellationToken)
        {
            budget.CheckAvailable(AuditBytes(value, cancellationToken), cancellationToken);
            var body = budget.Reservation.AuditCapture.CaptureJson(value, cancellationToken);
            budget._auditAreaStarted = true;
            return body;
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

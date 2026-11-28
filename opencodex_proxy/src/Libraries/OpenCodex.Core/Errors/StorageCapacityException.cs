using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.ProtocolConversion.Protocols;

namespace OpenCodex.Core.Errors;

/// <summary>A local request/storage limit, never evidence that an upstream channel is unhealthy.</summary>
public sealed class StorageCapacityException : ProxyException
{
    public StorageCapacityException(string message, int statusCode, string errorCode, Exception? innerException = null)
        : base(message, statusCode, innerException)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }

    public override string ErrorType => StatusCode == 413 ? "invalid_request_error" : "storage_error";

    public override object ToResponse() => new
    {
        error = new Dictionary<string, object?>
        {
            ["message"] = Message,
            ["type"] = ErrorType,
            ["code"] = ErrorCode
        }
    };
}

public static class ResourceErrors
{
    /// <summary>Only known local resource failures are normalized; ordinary upstream IO is unchanged.</summary>
    public static Exception Normalize(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            StorageCapacityException => exception,
            RequestBudgetExceededException => new StorageCapacityException("request body too large", 413,
                "request_too_large", exception),
            BadRequestException { StatusCode: 413 } => new StorageCapacityException("request body too large", 413,
                "request_too_large", exception),
            SnapshotCapacityExceededException => new StorageCapacityException(
                "Complete snapshot storage capacity exceeded; no content was truncated.", 507,
                "snapshot_capacity_exceeded", exception),
            SnapshotPersistenceException => new StorageCapacityException(
                "Complete snapshot storage IO failed; state could not be committed.", 507,
                "snapshot_write_failed", exception),
            UpstreamException upstream when IsCaptureLimit(upstream.ErrorCode) => CaptureLimit(upstream, upstream.ErrorCode!),
            OpenCodex.ProtocolConversion.Errors.UpstreamException upstream when IsCaptureLimit(upstream.ErrorCode) =>
                CaptureLimit(upstream, upstream.ErrorCode!),
            _ => exception
        };
    }

    public static bool IsResourceFailure(Exception exception) => Normalize(exception) is StorageCapacityException;

    public static Exception Combine(Exception operationFailure, Exception auditFailure)
    {
        var normalized = Normalize(operationFailure);
        var both = new AggregateException("Request execution and complete audit persistence both failed.",
            operationFailure, auditFailure);
        if (normalized is StorageCapacityException capacity)
            return new StorageCapacityException(capacity.Message, capacity.StatusCode, capacity.ErrorCode, both);
        if (Normalize(auditFailure) is StorageCapacityException auditCapacity)
            return new StorageCapacityException(auditCapacity.Message, auditCapacity.StatusCode, auditCapacity.ErrorCode, both);
        return both;
    }

    private static bool IsCaptureLimit(string? errorCode) => errorCode is
        "output_capture_limit" or "evidence_capture_limit" or "response_capture_limit" or "response_capture_limit_exceeded";

    private static StorageCapacityException CaptureLimit(Exception exception, string errorCode) =>
        new(exception.Message, 507, errorCode, exception);
}

namespace OpenCodex.ProtocolConversion.Abstractions;

public interface IProtocolAuditCapture
{
    int MemoryThresholdBytes { get; }
    IProtocolAuditBuffer CreateStaging(CancellationToken cancellationToken);
    IProtocolAuditBody CaptureJson(object? value, CancellationToken cancellationToken);
}

public interface IProtocolAuditBuffer : IDisposable
{
    long Length { get; }
    Stream OpenSnapshotRead();
    void Write(ReadOnlySpan<byte> value);
}

public interface IProtocolAuditBody : IDisposable
{
    long Length { get; }
}

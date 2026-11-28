namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>A complete snapshot could not obtain its explicitly requested storage credits.</summary>
public sealed class SnapshotCapacityExceededException : IOException
{
    public SnapshotCapacityExceededException(long requestedBytes, long availableBytes)
        : base("Complete snapshot storage capacity exceeded; no content was truncated.")
    {
        RequestedBytes = requestedBytes;
        AvailableBytes = availableBytes;
    }

    public long RequestedBytes { get; }
    public long AvailableBytes { get; }
}

/// <summary>A private snapshot IO operation failed; the public message never exposes storage paths or content.</summary>
public sealed class SnapshotPersistenceException : IOException
{
    public SnapshotPersistenceException(IOException innerException)
        : base("Complete snapshot storage IO failed; state could not be committed.", innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
    }

    internal static IOException Wrap(IOException exception) => exception is SnapshotCapacityExceededException or SnapshotPersistenceException
        ? exception : new SnapshotPersistenceException(exception);
}

namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>A private snapshot IO operation failed; the public message never exposes storage paths or content.</summary>
public sealed class SnapshotPersistenceException : IOException
{
    public SnapshotPersistenceException(IOException innerException)
        : base("Complete snapshot storage IO failed; state could not be committed.", innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
    }

    internal static IOException Wrap(IOException exception) => exception is SnapshotPersistenceException
        ? exception : new SnapshotPersistenceException(exception);
}

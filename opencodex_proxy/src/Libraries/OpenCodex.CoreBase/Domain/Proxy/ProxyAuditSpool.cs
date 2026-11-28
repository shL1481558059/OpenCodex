using System.Security.Cryptography;
using System.Text;
using OpenCodex.ProtocolConversion;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.ProtocolConversion.Abstractions;
using OpenCodex.ProtocolConversion.Json;

namespace OpenCodex.CoreBase.Domain.Proxy;

public sealed class ProxyAuditSpoolOptions
{
    public string Directory { get; set; } = Path.Combine(Path.GetTempPath(), "opencodex-audit-spool");
    // Existing legacy small-body policy only. Explicit snapshots always use root-sized disk pages.
    public int MemoryThresholdBytes { get; set; } = 256 * 1024;
    // Compatibility for explicitly configured test fixtures, not production capacity sources.
    // Production binding must reject these retired keys. No default body/file gate remains.
    public long? MaxBodyBytes { get; set; }
    public long? MaxTotalBytes { get; set; }
    public int? MaxFiles { get; set; }
    public int OrphanAgeSeconds { get; set; } = 3600;
}

/// <summary>Tracks complete immutable snapshots, staging files, owners and metadata.
/// The dedicated directory also needs an OS quota if used by multiple server processes.</summary>
public sealed class ProxyAuditSpool : IProtocolAuditCapture, IDisposable
{
    // Kept only to make a missing explicit dependency fail clearly during migration; never creates a spool.
    internal static ProxyAuditSpool Default => throw new InvalidOperationException("An explicit shared audit spool is required.");
    private readonly object _gate = new();
    private readonly string _directory, _snapshotDirectory, _snapshotOwnerPath;
    private readonly Guid _snapshotInstance = Guid.NewGuid();
    private readonly long _containerMetadataBytes, _snapshotContainerMetadataBytes, _snapshotOwnerMetadataBytes;
    private const int SnapshotOwnerHeaderBytes = 8 + 16; // Version magic plus binary instance UUID, not a capacity policy.
    private static ReadOnlySpan<byte> SnapshotOwnerMagic => "OCXSNP01"u8;
    private FileStream? _snapshotOwnerLock;
    private IOException? _snapshotOwnerFailure;
    private bool _snapshotOwnerCharged;
    private int _activeReservations, _snapshotContainers;
    private readonly long? _legacyMaxBody, _legacyMaxTotal;
    private readonly int? _legacyMaxFiles;
    private readonly int _orphanAge, _memoryThreshold;
    private long _bytes, _metadata, _legacyBytes, _nextSweep;
    private int _files, _legacyFiles;
    private bool _disposed;
    private long _inheritedBytes, _inheritedMetadata, _inheritedOwnerBytes;
    private int _inheritedFiles, _inheritedOwners;
    private readonly HashSet<string> _ownedPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingFile> _pendingCleanup = new(StringComparer.Ordinal);
    private readonly record struct PendingFile(long PhysicalBytes, long MetadataBytes, bool Legacy);
    internal Func<string, FileStreamOptions, Stream> CreateFile { get; init; } = (path, options) => new FileStream(path, options);
    internal int MemoryThresholdBytes => _memoryThreshold;
    internal object QuotaGate => _gate;
    internal long SnapshotContainerMetadataBytes => _snapshotContainerMetadataBytes;
    internal (long PhysicalBytes, long MetadataBytes, int Files, int Containers) SnapshotUsage
    {
        get { lock (_gate) return (checked(_bytes + _inheritedBytes),
            checked(_metadata + _inheritedMetadata), checked(_files + _inheritedFiles - _inheritedOwners - (_snapshotOwnerCharged ? 1 : 0)),
            _snapshotContainers); }
    }

    public ProxyAuditSpool(ProxyAuditSpoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MemoryThresholdBytes < 0 || options.MaxBodyBytes is <= 0 or > int.MaxValue
            || options.MaxTotalBytes is <= 0 || options.MaxFiles is <= 0 || options.OrphanAgeSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Directory));
        _snapshotDirectory = Path.Combine(_directory, $"snapshot-{_snapshotInstance:N}");
        _snapshotOwnerPath = Path.Combine(_snapshotDirectory, "owner.lock");
        _containerMetadataBytes = ContainerMetadataFor(_directory);
        _snapshotContainerMetadataBytes = ContainerMetadataFor(_snapshotDirectory);
        _snapshotOwnerMetadataBytes = OwnerMetadataFor(_snapshotDirectory);
        _legacyMaxBody = options.MaxBodyBytes; _legacyMaxTotal = options.MaxTotalBytes;
        _legacyMaxFiles = options.MaxFiles; _orphanAge = options.OrphanAgeSeconds;
        _memoryThreshold = options.MemoryThresholdBytes;
        try
        {
            if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(_directory);
            else System.IO.Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            CleanupExpired();
        }
        catch (IOException exception) { throw SnapshotPersistenceException.Wrap(exception); }
    }
    // Legacy observations describe payload bodies; the new public ledger also reports owner-lock charges.
    internal (long Bytes, int Files) Usage
    {
        get { lock (_gate) return (checked(_bytes + _inheritedBytes - _inheritedOwnerBytes - (_snapshotOwnerCharged ? SnapshotOwnerHeaderBytes : 0)),
            checked(_files + _inheritedFiles - _inheritedOwners - (_snapshotOwnerCharged ? 1 : 0))); }
    }

    private long ContainerMetadataFor(string directory) => checked(SnapshotStore.ContainerMetadataFieldsBytes
        + Encoding.UTF8.GetByteCount(Path.Combine(directory, "audit-" + Guid.Empty.ToString("N") + ".tmp")));

    private long OwnerMetadataFor(string directory) => checked(SnapshotStore.ContainerMetadataFieldsBytes
        + Encoding.UTF8.GetByteCount(Path.Combine(directory, "owner.lock"))
        + 16 + sizeof(int) + Encoding.UTF8.GetByteCount(directory));

    internal void InitializeReservation(SnapshotReservation reservation)
    {
        try { if (Environment.TickCount64 >= Interlocked.Read(ref _nextSweep)) CleanupExpired(); }
        catch (IOException exception) { throw SnapshotPersistenceException.Wrap(exception); }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeReservations = checked(_activeReservations + 1);
        }
    }

    internal void ConsumeSnapshot(long bytes, bool metadata)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (metadata) _metadata = checked(_metadata + bytes);
            else _bytes = checked(_bytes + bytes);
        }
    }

    internal void RefundSnapshot(long bytes, bool metadata)
    {
        lock (_gate)
        {
            if (metadata) _metadata -= bytes; else _bytes -= bytes;
        }
    }

    internal bool CompleteReservation(SnapshotReservation reservation)
    {
        lock (_gate)
        {
            if (reservation.Disposed) return false;
            reservation.Disposed = true;
            _activeReservations--;
            ReleaseSnapshotOwnerIfIdle();
            return true;
        }
    }

    internal string AllocateSnapshotPath(long metadataBytes)
    {
        var path = Path.Combine(_snapshotDirectory, $"audit-{Guid.NewGuid():N}.tmp");
        lock (_gate)
        {
            EnsureSnapshotOwner();
            ConsumeSnapshot(metadataBytes, metadata: true);
            try
            {
                _ownedPaths.Add(path); _files = checked(_files + 1);
                _snapshotContainers = checked(_snapshotContainers + 1);
            }
            catch
            {
                _ownedPaths.Remove(path);
                RefundSnapshot(metadataBytes, metadata: true);
                throw;
            }
        }
        return path;
    }

    internal Stream CreateSnapshotStorage(string path)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        // BufferSize=1 disables FileStream payload buffering. Page payload size comes from root FlushBytes.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.Read, Options = FileOptions.RandomAccess, BufferSize = 1 };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return CreateFile(path, options); }
        catch (IOException exception) { throw SnapshotPersistenceException.Wrap(exception); }
    }

    internal void RetireSnapshotContainer(string path, long physicalBytes, long metadataBytes)
    {
        var deleted = DeleteOwnedPath(path);
        lock (_gate)
        {
            _snapshotContainers--;
            if (!deleted)
                _pendingCleanup[path] = new(physicalBytes, metadataBytes, Legacy: false);
            else
            {
                RefundSnapshot(physicalBytes, metadata: false);
                RefundSnapshot(metadataBytes, metadata: true);
                _files--; _ownedPaths.Remove(path);
            }
            ReleaseSnapshotOwnerIfIdle();
        }
    }

    private static bool DeleteOwnedPath(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void EnsureSnapshotOwner()
    {
        if (_snapshotOwnerFailure is { } failure)
            throw new IOException("Private snapshot owner initialization failed.", failure);
        if (_snapshotOwnerLock is not null) return;
        ConsumeSnapshot(_snapshotOwnerMetadataBytes, metadata: true);
        ConsumeSnapshot(SnapshotOwnerHeaderBytes, metadata: false);
        _snapshotOwnerCharged = true; _files = checked(_files + 1);
        FileStream? lifetime = null;
        try
        {
            if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(_snapshotDirectory);
            else System.IO.Directory.CreateDirectory(_snapshotDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            lifetime = OpenOwnerLock(_snapshotOwnerPath, FileMode.CreateNew);
            WriteOwnerHeader(lifetime, _snapshotInstance);
            _snapshotOwnerLock = lifetime;
        }
        catch (IOException exception)
        {
            _snapshotOwnerLock = lifetime; _snapshotOwnerFailure = exception;
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            _snapshotOwnerLock = lifetime;
            _snapshotOwnerFailure = new IOException("Private snapshot owner initialization failed.", exception);
            throw _snapshotOwnerFailure;
        }
    }

    private static FileStream OpenOwnerLock(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite,
            Share = FileShare.None, Options = FileOptions.None, BufferSize = 1 };
        if (!OperatingSystem.IsWindows() && mode == FileMode.CreateNew)
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static void WriteOwnerHeader(FileStream target, Guid instance)
    {
        Span<byte> header = stackalloc byte[SnapshotOwnerHeaderBytes];
        SnapshotOwnerMagic.CopyTo(header);
        if (!instance.TryWriteBytes(header[SnapshotOwnerMagic.Length..]))
            throw new InvalidOperationException("Snapshot owner identity layout is invalid.");
        target.Write(header); target.Flush();
    }

    private void ReleaseSnapshotOwnerIfIdle()
    {
        if ((!_disposed && _snapshotOwnerFailure is null) || _activeReservations != 0 || _snapshotContainers != 0)
            return;
        if (!_snapshotOwnerCharged) return;
        // Disposing the spool stops new admission, but the instance lock stays held until all
        // independently owned containers (including readers opened before log commit) are gone.
        var lifetime = _snapshotOwnerLock; _snapshotOwnerLock = null;
        try { lifetime?.Dispose(); }
        catch (IOException exception) { _snapshotOwnerFailure = exception; }
        if (TryCleanupSnapshotDirectory(_snapshotDirectory, ignoreAge: true, trustedOwn: true))
            ReleaseSnapshotOwnerCharges();
    }

    private void ReleaseSnapshotOwnerCharges()
    {
        if (!_snapshotOwnerCharged) return;
        // Directory deletion proves all of its failed-delete files are gone as well.
        foreach (var path in _pendingCleanup.Keys.Where(path => Path.GetDirectoryName(path) == _snapshotDirectory).ToArray())
            ReleasePending(path);
        _bytes -= SnapshotOwnerHeaderBytes; _metadata -= _snapshotOwnerMetadataBytes;
        _files--; _snapshotOwnerCharged = false; _snapshotOwnerFailure = null;
    }

    private bool TryCleanupSnapshotDirectory(string directory, bool ignoreAge, bool trustedOwn)
    {
        var name = Path.GetFileName(directory);
        if (name.Length != 41 || !name.StartsWith("snapshot-", StringComparison.Ordinal)
            || !Guid.TryParseExact(name.AsSpan(9, 32), "N", out var instance)
            || !string.Equals(Path.GetDirectoryName(directory), _directory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return false;
        FileStream? exclusion = null;
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            if (!ignoreAge && System.IO.Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddSeconds(-_orphanAge))
                return false;
            var ownerPath = Path.Combine(directory, "owner.lock");
            try { exclusion = OpenOwnerLock(ownerPath, FileMode.Open); }
            catch (FileNotFoundException)
            {
                // A crash before creating the lock can leave an empty managed directory. Never
                // claim a nonempty unmarked directory or delete unrelated user material.
                if (System.IO.Directory.EnumerateFileSystemEntries(directory).Any()) return false;
                exclusion = OpenOwnerLock(ownerPath, FileMode.CreateNew);
                WriteOwnerHeader(exclusion, instance);
            }
            if (!trustedOwn)
            {
                if (exclusion.Length == SnapshotOwnerHeaderBytes)
                {
                    Span<byte> header = stackalloc byte[SnapshotOwnerHeaderBytes];
                    exclusion.Position = 0; exclusion.ReadExactly(header);
                    if (!header[..SnapshotOwnerMagic.Length].SequenceEqual(SnapshotOwnerMagic)
                        || new Guid(header[SnapshotOwnerMagic.Length..]) != instance) return false;
                }
                else if (exclusion.Length >= SnapshotOwnerHeaderBytes
                    || System.IO.Directory.EnumerateFileSystemEntries(directory)
                        .Any(path => !string.Equals(path, ownerPath, StringComparison.Ordinal))) return false;
            }
            // A successful exclusive instance lock is the prerequisite for every payload delete.
            // A live spool/reader keeps this acquisition failing even when payload writer FDs close.
            foreach (var path in System.IO.Directory.EnumerateFiles(directory, "audit-*.tmp"))
            {
                var fileName = Path.GetFileName(path);
                if (fileName.Length != 42 || !Guid.TryParseExact(fileName.AsSpan(6, 32), "N", out _)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                File.Delete(path); ReleasePending(path);
            }
            exclusion.Dispose(); exclusion = null;
            File.Delete(ownerPath);
            System.IO.Directory.Delete(directory, recursive: false);
            return true;
        }
        catch (DirectoryNotFoundException) { return true; }
        catch (FileNotFoundException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally
        {
            try { exclusion?.Dispose(); }
            catch (IOException) { /* Keep its directory/physical charges until a later sweep. */ }
        }
    }
    public ProxyLogBodyHandle CaptureJson(object? value, CancellationToken cancellationToken = default)
    {
        var body=CreateStaging(cancellationToken);
        try
        {
            using(var writer=new Utf8JsonWriter(body,new() { Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            { WriteJson(writer,value,0);writer.Flush(); }
            body.Seal();return ProxyLogBodyHandle.FromSpool(body);
        }
        catch { body.Dispose();throw; }
    }

    public ProxyLogBodyHandle CaptureUtf8(ReadOnlySpan<byte> value, CancellationToken cancellationToken = default)
    {
        var body=CreateStaging(cancellationToken);
        try
        {
            body.Write(value);
            body.Seal();
            return ProxyLogBodyHandle.FromSpool(body);
        }
        catch { body.Dispose();throw; }
    }

    // A request-owned append buffer shares the same lifecycle and crash cleanup
    // as immutable JSON snapshots. Only Core can use this; callers must Dispose in all paths.
    internal AuditFile CreateStaging(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { if (Environment.TickCount64 >= Interlocked.Read(ref _nextSweep)) CleanupExpired(); }
        catch (IOException exception) { throw SnapshotPersistenceException.Wrap(exception); }
        var path = Path.Combine(_directory, $"audit-{Guid.NewGuid():N}.tmp");
        var metadata = checked(_containerMetadataBytes + SnapshotStore.BufferMetadataBytes);
        lock (_gate)
        {
            if (_legacyMaxFiles is { } files && _legacyFiles + _inheritedFiles >= files)
                throw new IOException("Complete audit spool file quota exceeded.");
            _metadata = checked(_metadata + metadata);
            _files++; _legacyFiles++; _ownedPaths.Add(path);
        }
        try { return new AuditFile(this, path, cancellationToken, metadata); }
        catch { ReleaseLegacy(path, 0, 0, 0, metadata); throw; }
    }

    int IProtocolAuditCapture.MemoryThresholdBytes => _memoryThreshold;

    IProtocolAuditBuffer IProtocolAuditCapture.CreateStaging(CancellationToken cancellationToken)
    {
        return new ProtocolAuditBuffer(CreateStaging(cancellationToken));
    }

    IProtocolAuditBody IProtocolAuditCapture.CaptureJson(object? value, CancellationToken cancellationToken)
    {
        var body = CreateStaging(cancellationToken);
        try
        {
            using (var writer = new Utf8JsonWriter(body, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                WriteJson(writer, value, 0);
                writer.Flush();
            }
            body.Seal();
            return new ProtocolAuditBody(body);
        }
        catch
        {
            body.Dispose();
            throw;
        }
    }

    private sealed class ProtocolAuditBuffer(AuditFile inner) : IProtocolAuditBuffer
    {
        public long Length => inner.Length;
        public Stream OpenSnapshotRead() => inner.OpenSnapshotRead();
        public void Write(ReadOnlySpan<byte> value) => inner.Write(value);
        public void Dispose() => inner.Dispose();
    }

    private sealed class ProtocolAuditBody(AuditFile inner) : IProtocolAuditBody
    {
        public long Length => inner.Length;
        public void Dispose() => inner.Dispose();
    }
    private Stream CreateStorage(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.Read, Options = FileOptions.DeleteOnClose | FileOptions.SequentialScan, BufferSize = 1 };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return CreateFile(path, options); }
        catch (IOException exception) { throw SnapshotPersistenceException.Wrap(exception); }
    }

    private void ReserveLegacy(int count, long bodyLength, long physicalGrowth)
    {
        lock (_gate)
        {
            if (_legacyMaxBody is { } body && count > body - bodyLength
                || _legacyMaxTotal is { } total && count > total - _legacyBytes - _inheritedBytes)
                throw new IOException("Complete audit spool byte quota exceeded; body was not truncated.");
            _legacyBytes = checked(_legacyBytes + count);
            _bytes = checked(_bytes + physicalGrowth);
        }
    }

    private void RefundPhysical(long bytes)
    {
        lock (_gate) _bytes -= bytes;
    }

    private void ReleaseLegacy(string path, long logicalBytes, long physicalBytes, long memoryBytes, long metadataBytes)
    {
        var deleted = DeleteOwnedPath(path);
        lock (_gate)
        {
            _legacyBytes -= logicalBytes;
            if (!deleted)
            {
                _bytes -= memoryBytes; // RAM is gone; the undeleted private file is still charged.
                _pendingCleanup[path] = new(physicalBytes - memoryBytes, metadataBytes, Legacy: true);
                return;
            }
            _bytes -= physicalBytes; _metadata -= metadataBytes;
            _files--; _legacyFiles--; _ownedPaths.Remove(path);
        }
    }

    private void ReleasePending(string path)
    {
        lock (_gate)
        {
            if (!_pendingCleanup.Remove(path, out var pending)) return;
            _bytes -= pending.PhysicalBytes; _metadata -= pending.MetadataBytes;
            _files--; if (pending.Legacy) _legacyFiles--;
            _ownedPaths.Remove(path);
        }
    }

    /// <summary>
    /// Only exact managed filenames are swept. Same-process active containers are skipped even
    /// when sealed writer FDs have closed; read/history ownership, not TTL, protects their bytes.
    /// Failed deletion retains its physical and descriptor charges until a later successful sweep.
    /// </summary>
    public int CleanupExpired()
    {
        Interlocked.Exchange(ref _nextSweep, Environment.TickCount64 + 300000);
        var deleted = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "audit-*.tmp"))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 42 || !Guid.TryParseExact(name.AsSpan(6, 32), "N", out _)) continue;
            lock (_gate)
                if (_ownedPaths.Contains(path) && !_pendingCleanup.ContainsKey(path)) continue;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddSeconds(-_orphanAge)) continue;
                // Exclusion also protects readers held by another live process. Deployments that
                // share this directory require the documented OS quota/process-lifetime policy.
                using (var orphan = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.DeleteOnClose)) { }
                File.Delete(path);
                ReleasePending(path);
                deleted++;
            }
            catch (FileNotFoundException) { ReleasePending(path); }
            catch (IOException) { /* Active reader or IO failure: never delete around its lock. */ }
            catch (UnauthorizedAccessException) { /* Keep capacity charged on a real permission failure. */ }
        }
        lock (_gate)
        {
            // A DeleteOnClose/external cleanup can remove a pending file between sweeps.
            foreach (var path in _pendingCleanup.Keys.ToArray())
            {
                try { _ = File.GetAttributes(path); }
                catch (FileNotFoundException) { ReleasePending(path); }
                catch (DirectoryNotFoundException) { ReleasePending(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (var directory in System.IO.Directory.EnumerateDirectories(_directory, "snapshot-*"))
            {
                var trustedOwn = directory == _snapshotDirectory && _snapshotOwnerCharged
                    && _snapshotOwnerLock is null && _activeReservations == 0 && _snapshotContainers == 0;
                if (TryCleanupSnapshotDirectory(directory, ignoreAge: trustedOwn, trustedOwn)
                    && directory == _snapshotDirectory && _snapshotOwnerLock is null)
                    ReleaseSnapshotOwnerCharges();
            }
            ReleaseSnapshotOwnerIfIdle();
            long inheritedBytes = 0, inheritedMetadata = 0, inheritedOwnerBytes = 0;
            var inheritedFiles = 0;
            var inheritedOwners = 0;
            foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "audit-*.tmp"))
            {
                if (_ownedPaths.Contains(path)) continue;
                var name = Path.GetFileName(path);
                if (name.Length != 42 || !Guid.TryParseExact(name.AsSpan(6, 32), "N", out _)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    inheritedBytes = checked(inheritedBytes + info.Length);
                    inheritedMetadata = checked(inheritedMetadata + _containerMetadataBytes);
                    inheritedFiles = checked(inheritedFiles + 1);
                }
                catch (FileNotFoundException) { }
            }
            foreach (var directory in System.IO.Directory.EnumerateDirectories(_directory, "snapshot-*"))
            {
                var name = Path.GetFileName(directory);
                if (name.Length != 41 || !Guid.TryParseExact(name.AsSpan(9, 32), "N", out _)) continue;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    var own = directory == _snapshotDirectory && _snapshotOwnerCharged;
                    if (!own)
                    {
                        inheritedMetadata = checked(inheritedMetadata + OwnerMetadataFor(directory));
                        inheritedFiles = checked(inheritedFiles + 1); inheritedOwners = checked(inheritedOwners + 1);
                        try
                        {
                            var owner = new FileInfo(Path.Combine(directory, "owner.lock"));
                            if ((owner.Attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                inheritedBytes = checked(inheritedBytes + owner.Length);
                                inheritedOwnerBytes = checked(inheritedOwnerBytes + owner.Length);
                            }
                        }
                        catch (FileNotFoundException) { }
                    }
                    foreach (var path in System.IO.Directory.EnumerateFiles(directory, "audit-*.tmp"))
                    {
                        if (_ownedPaths.Contains(path)) continue;
                        var fileName = Path.GetFileName(path);
                        if (fileName.Length != 42 || !Guid.TryParseExact(fileName.AsSpan(6, 32), "N", out _)) continue;
                        try
                        {
                            var info = new FileInfo(path);
                            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            inheritedBytes = checked(inheritedBytes + info.Length);
                            inheritedMetadata = checked(inheritedMetadata + ContainerMetadataFor(directory));
                            inheritedFiles = checked(inheritedFiles + 1);
                        }
                        catch (FileNotFoundException) { }
                    }
                }
                catch (DirectoryNotFoundException) { }
            }
            _inheritedBytes = inheritedBytes; _inheritedMetadata = inheritedMetadata;
            _inheritedFiles = inheritedFiles; _inheritedOwnerBytes = inheritedOwnerBytes;
            _inheritedOwners = inheritedOwners;
        }
        return deleted;
    }

    /// <summary>
    /// Stops new admissions. Existing reservations, log bodies, leases and readers still own
    /// their bytes and can release them normally; shutdown never invalidates a live reader.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ReleaseSnapshotOwnerIfIdle();
        }
    }

    internal static void WriteJson(Utf8JsonWriter writer, object? value, int depth) =>
        ProtocolJsonWriter.Write(writer, value, depth);

    internal sealed class AuditFile : Stream
    {
        private readonly object _gate = new();
        private readonly ProxyAuditSpool? _owner;
        private readonly string _path;
        private readonly CancellationToken _cancellationToken;
        private readonly long _metadataBytes;
        private readonly IncrementalHash? _hash;
        private Stream? _storage;
        private MemoryStream? _retiredMemory;
        private SnapshotLease? _lease;
        private long _length, _logicalCharge, _diskCharge, _memoryCharge;
        private int _readers, _memoryReaders;
        private bool _disposed, _sealed, _faulted, _retired;

        internal AuditFile(ProxyAuditSpool owner, string path, CancellationToken cancellationToken, long metadataBytes)
        {
            _owner = owner; _path = path; _cancellationToken = cancellationToken; _metadataBytes = metadataBytes;
            _storage = new MemoryStream();
            _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        private AuditFile(SnapshotLease ownedLease)
        {
            _lease = ownedLease; _path = ownedLease.PathForTests;
            _length = ownedLease.Length; Hash = ownedLease.Hash; _sealed = true;
        }

        internal static AuditFile FromLease(SnapshotLease lease)
        {
            var owned = lease.AddRef();
            try { return new AuditFile(owned); }
            catch { owned.Dispose(); throw; }
        }

        internal string Hash { get; private set; } = "";
        internal string PathForTests => _path;

        internal void Seal()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sealed) return;
                if (_faulted) throw new IOException("Private audit storage has failed; the body cannot be sealed.");
                _cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _storage!.Flush();
                    Hash = Convert.ToHexStringLower(_hash!.GetHashAndReset());
                    _sealed = true;
                }
                catch (IOException exception) { throw SnapshotPersistenceException.Wrap(exception); }
            }
        }

        internal Stream OpenRead()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_sealed) throw new InvalidOperationException("Audit body is not sealed.");
                return OpenSnapshotRead();
            }
        }

        internal Stream OpenSnapshotRead()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_lease is { } lease) return lease.Read();
                Stream source;
                MemoryStream? memory;
                try
                {
                    _storage!.Flush();
                    memory = _storage as MemoryStream;
                    source = memory is not null
                        ? new MemoryStream(memory.GetBuffer(), 0, checked((int)_length), writable: false)
                        : new FileStream(_path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
                }
                catch (IOException exception) { throw SnapshotPersistenceException.Wrap(exception); }
                try
                {
                    var reader = new AuditReadOwner(source, this, _length, memory is not null);
                    _readers = checked(_readers + 1);
                    if (memory is not null) _memoryReaders = checked(_memoryReaders + 1);
                    return reader;
                }
                catch { source.Dispose(); throw; }
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sealed) throw new InvalidOperationException("Audit body is sealed.");
                if (_faulted) throw new IOException("Private audit storage has failed.");
                _cancellationToken.ThrowIfCancellationRequested();
                if (buffer.IsEmpty) return;
                var length = checked(_length + buffer.Length);
                var memory = _storage as MemoryStream;
                // A live prefix reader retains the old array. Spill before any future replacement,
                // and keep that array's charge until the reader's independent owner is released.
                var spill = memory is not null && (length > _owner!._memoryThreshold || _memoryReaders > 0);
                var growth = spill ? length
                    : memory is null ? buffer.Length
                    : length > memory.Capacity ? length : 0;
                _owner!.ReserveLegacy(buffer.Length, _length, growth);
                _logicalCharge = checked(_logicalCharge + buffer.Length);
                try
                {
                    if (spill)
                    {
                        _diskCharge = checked(_diskCharge + growth);
                        var disk = _owner.CreateStorage(_path);
                        _storage = disk; _retiredMemory = memory;
                        // Write directly from the retained array: no unbilled CopyTo scratch buffer.
                        disk.Write(memory!.GetBuffer().AsSpan(0, checked((int)_length)));
                    }
                    else if (memory is not null && growth > 0)
                    {
                        var previousCapacity = memory.Capacity;
                        // Exact growth instead of MemoryStream's implicit geometric capacity. Both
                        // arrays are charged during replacement, then the released one is refunded.
                        try { memory.Capacity = checked((int)length); }
                        catch { _owner.RefundPhysical(growth); throw; }
                        _memoryCharge = checked(_memoryCharge + growth - previousCapacity);
                        _owner.RefundPhysical(previousCapacity);
                    }
                    else if (memory is null) _diskCharge = checked(_diskCharge + growth);
                    _storage!.Write(buffer);
                    _hash!.AppendData(buffer); _length = length;
                    ReleaseRetiredMemoryIfUnused();
                }
                catch (IOException exception)
                {
                    _faulted = true;
                    // A failed file write can be partial. Offered physical bytes stay charged
                    // until its final owner proves deletion, rather than being refunded here.
                    throw SnapshotPersistenceException.Wrap(exception);
                }
                catch { _faulted = true; throw; }
            }
        }

        private void ReleaseRetiredMemoryIfUnused()
        {
            if (_memoryReaders != 0 || _retiredMemory is not { } memory) return;
            var capacity = memory.Capacity;
            _retiredMemory = null; memory.Dispose();
            _memoryCharge -= capacity; _owner!.RefundPhysical(capacity);
        }

        internal void ReleaseReader(bool memory)
        {
            lock (_gate)
            {
                _readers--; if (memory) _memoryReaders--;
                ReleaseRetiredMemoryIfUnused();
                if (_disposed && _readers == 0) Retire();
            }
        }

        // JSON Flush is not fsync per leaf; Seal/OpenRead flush storage once at the boundary.
        public override void Flush() => _cancellationToken.ThrowIfCancellationRequested();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    if (_lease is { } lease) { _lease = null; lease.Dispose(); }
                    else if (_readers == 0) Retire();
                }
            }
            base.Dispose(disposing);
        }

        private void Retire()
        {
            if (_retired) return;
            _retired = true;
            try { _storage?.Dispose(); }
            finally
            {
                try { _retiredMemory?.Dispose(); }
                finally
                {
                    _storage = null; _retiredMemory = null; _hash?.Dispose();
                    _owner!.ReleaseLegacy(_path, _logicalCharge, checked(_diskCharge + _memoryCharge),
                        _memoryCharge, _metadataBytes);
                }
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite { get { lock (_gate) return !_disposed && !_sealed && !_faulted; } }
        public override long Length { get { lock (_gate) return _length; } }
        public override long Position { get => Length; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class AuditReadOwner(Stream source, AuditFile owner, long length, bool memory) : Stream
    {
        private AuditFile? _owner = owner;
        private void EnsureLive() => ObjectDisposedException.ThrowIf(_owner is null, this);
        public override int Read(Span<byte> buffer)
        {
            EnsureLive();
            try
            {
                var remaining = length - source.Position;
                return remaining <= 0 ? 0 : source.Read(buffer[..checked((int)Math.Min(buffer.Length, remaining))]);
            }
            catch (IOException exception) { throw SnapshotPersistenceException.Wrap(exception); }
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            EnsureLive();
            var position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(source.Position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (position < 0) throw new IOException("Cannot seek before the audit snapshot.");
            return source.Seek(position, SeekOrigin.Begin);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _owner, null) is { } held)
            {
                try { source.Dispose(); }
                finally { held.ReleaseReader(memory); }
            }
            base.Dispose(disposing);
        }
        public override bool CanRead => _owner is not null;
        public override bool CanSeek => _owner is not null && source.CanSeek;
        public override bool CanWrite => false;
        public override long Length { get { EnsureLive(); return length; } }
        public override long Position { get { EnsureLive(); return source.Position; } set => Seek(value, SeekOrigin.Begin); }
        public override void Flush() => EnsureLive();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.Domain.Proxy;

public sealed class ProxyAuditSpoolOptions
{
    public string Directory { get; set; } = Path.Combine(Path.GetTempPath(), "opencodex-audit-spool");
    public int MemoryThresholdBytes { get; set; } = 256 * 1024;
    public long MaxBodyBytes { get; set; } = 64L * 1024 * 1024;
    public long MaxTotalBytes { get; set; } = 1024L * 1024 * 1024;
    public int MaxFiles { get; set; } = 256;
    public int OrphanAgeSeconds { get; set; } = 3600;
}

/// <summary>Process-wide quota for complete, immutable JSON audit snapshots; never truncates.
/// The dedicated directory must also have an OS quota when shared by multiple server processes.</summary>
public sealed class ProxyAuditSpool
{
    private static readonly Lazy<ProxyAuditSpool> Fallback = new(() => new(new()));
    internal static ProxyAuditSpool Default => Fallback.Value;
    private static readonly JsonSerializerOptions JsonOptions = new()
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new SegmentedStringConverter() } };
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBody, _maxTotal;
    private readonly int _maxFiles, _orphanAge, _memoryThreshold;
    private long _bytes, _nextSweep;
    private int _files;
    private long _inheritedBytes;
    private int _inheritedFiles;
    private readonly HashSet<string> _ownedPaths = new(StringComparer.Ordinal);
    internal Func<string, FileStreamOptions, Stream> CreateFile { get; init; } = (path, options) => new FileStream(path, options);
    public ProxyAuditSpool(ProxyAuditSpoolOptions options)
    {
        if (options.MemoryThresholdBytes < 0 || options.MaxBodyBytes <= 0 || options.MaxBodyBytes > int.MaxValue || options.MaxTotalBytes <= 0 || options.MaxFiles <= 0 || options.OrphanAgeSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _directory = Path.GetFullPath(options.Directory);
        _maxBody = options.MaxBodyBytes; _maxTotal = options.MaxTotalBytes;
        _maxFiles = options.MaxFiles; _orphanAge = options.OrphanAgeSeconds; _memoryThreshold = options.MemoryThresholdBytes;
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(_directory);
        else System.IO.Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CleanupExpired();
    }
    internal (long Bytes, int Files) Usage { get { lock (_gate) return (_bytes + _inheritedBytes, _files + _inheritedFiles); } }
    public ProxyLogBodyHandle CaptureJson(object? value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Environment.TickCount64 >= Interlocked.Read(ref _nextSweep)) CleanupExpired();
        var path = Path.Combine(_directory, $"audit-{Guid.NewGuid():N}.tmp");
        lock (_gate)
        {
            if (_files + _inheritedFiles >= _maxFiles) throw new IOException("Complete audit spool file quota exceeded.");
            _files++;_ownedPaths.Add(path);
        }
        AuditFile? body = null;
        try
        {
            body = new AuditFile(this, path, cancellationToken);
            using (var writer = new Utf8JsonWriter(body,new() { Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                WriteJson(writer,value,0);
                writer.Flush();
            }
            body.Seal();
            return ProxyLogBodyHandle.FromSpool(body);
        }
        catch { if (body is not null) body.Dispose(); else Release(0, true, path); throw; }
    }
    private Stream CreateStorage(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.Read, Options = FileOptions.DeleteOnClose | FileOptions.SequentialScan, BufferSize = 16384 };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return CreateFile(path,options);
    }
    private void Reserve(int count, long bodyLength)
    {
        lock (_gate)
        {
            if (count > _maxBody - bodyLength || count > _maxTotal - _bytes - _inheritedBytes)
                throw new IOException("Complete audit spool byte quota exceeded; body was not truncated.");
            _bytes += count;
        }
    }
    private void Release(long bytes, bool file, string? path=null)
    { lock (_gate) { _bytes -= bytes; if (file) { _files--;if(path is not null)_ownedPaths.Remove(path); } } }

    /// <summary>Only this spool's exact filenames, old and exclusively openable. Live files are
    /// locked even after the age threshold. Normal disposal uses DeleteOnClose; the sweep reclaims crash leftovers.</summary>
    public int CleanupExpired()
    {
        Interlocked.Exchange(ref _nextSweep, Environment.TickCount64 + 300000);
        var deleted = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "audit-*.tmp"))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 42 || !Guid.TryParseExact(name.AsSpan(6, 32), "N", out _)) continue;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddSeconds(-_orphanAge)) continue;
                // Acquire exclusion BEFORE deleting; FileShare.None refuses active leases/readers.
                using var orphan = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.DeleteOnClose);
                deleted++;
            }
            catch (FileNotFoundException) { }
            catch (IOException) { /* Active lease or external filesystem failure; never delete around its lock. */ }
        }
        // Crash leftovers younger than the TTL still consume admission quota after restart.
        // Enumerate without materializing a file list; never starve orphans after a fixed prefix.
        lock (_gate)
        {
            _inheritedBytes=0;_inheritedFiles=0;
            foreach(var path in System.IO.Directory.EnumerateFiles(_directory,"audit-*.tmp"))
            {
                if(_ownedPaths.Contains(path))continue;
                var name=Path.GetFileName(path);
                if(name.Length != 42 || !Guid.TryParseExact(name.AsSpan(6,32),"N",out _))continue;
                try
                {
                    var info=new FileInfo(path);
                    if((info.Attributes & FileAttributes.ReparsePoint)!=0)continue;
                    _inheritedBytes=checked(_inheritedBytes+info.Length);_inheritedFiles++;
                }
                catch(FileNotFoundException) { }
            }
        }
        return deleted;
    }

    // Normalized protocol graphs are dictionaries/lists. Flush each bounded leaf so a
    // long numeric/empty-item history cannot grow the writer's pending buffer indefinitely.
    private static void WriteJson(Utf8JsonWriter writer,object? value,int depth)
    {
        if(depth>64)throw new JsonException("Audit JSON exceeds maximum depth or contains a cycle.");
        if(value is IReadOnlyDictionary<string,object?> dictionary)
        {
            writer.WriteStartObject();
            foreach(var pair in dictionary) { writer.WritePropertyName(pair.Key);WriteJson(writer,pair.Value,depth+1); }
            writer.WriteEndObject();
        }
        else if(value is IEnumerable<object?> items)
        {
            writer.WriteStartArray();foreach(var item in items)WriteJson(writer,item,depth+1);writer.WriteEndArray();
        }
        else JsonSerializer.Serialize(writer,value,JsonOptions);
        writer.Flush();
    }

    // Serialize directly to the quota stream, rather than Serializer's whole-token pooled
    // buffer. Large ordinary string values are escaped in bounded segments (same JSON bytes).
    private sealed class SegmentedStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options) => throw new NotSupportedException();
        public override void WriteAsPropertyName(Utf8JsonWriter writer,string value,JsonSerializerOptions options) => writer.WritePropertyName(value);
        public override void Write(Utf8JsonWriter writer,string value,JsonSerializerOptions options)
        {
            if(value.Length==0) { writer.WriteStringValue("");writer.Flush();return; }
            for(var offset=0;offset<value.Length;)
            {
                var count=Math.Min(4096,value.Length-offset);
                if(offset+count<value.Length && char.IsHighSurrogate(value[offset+count-1]))count--;
                writer.WriteStringValueSegment(value.AsSpan(offset,count),offset+count==value.Length);
                writer.Flush();offset+=count;
            }
        }
    }

    internal sealed class AuditFile(ProxyAuditSpool owner, string path, CancellationToken cancellationToken) : Stream
    {
        private Stream _storage = new MemoryStream();
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        private bool _disposed, _sealed;
        internal string Hash { get; private set; } = "";
        internal string PathForTests => path;
        internal void Seal() { Flush(); _storage.Flush(); Hash = Convert.ToHexStringLower(_hash.GetHashAndReset()); _sealed = true; }
        internal Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sealed) throw new InvalidOperationException("Audit body is not sealed.");
            if (_storage is MemoryStream memory)
                return new MemoryStream(memory.GetBuffer(),0,checked((int)_length),writable:false);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384, FileOptions.SequentialScan);
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset,count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sealed) throw new InvalidOperationException("Audit body is sealed.");
            cancellationToken.ThrowIfCancellationRequested();
            owner.Reserve(buffer.Length, _length);
            try
            {
                if (_storage is MemoryStream memory && buffer.Length > owner._memoryThreshold - _length)
                {
                    var disk=owner.CreateStorage(path);
                    try { memory.Position=0;memory.CopyTo(disk); }
                    catch { disk.Dispose();throw; }
                    _storage=disk;memory.Dispose();
                }
                _storage.Write(buffer); _hash.AppendData(buffer); _length += buffer.Length;
            }
            catch { owner.Release(buffer.Length, false); throw; }
        }
        // Flush from JSON commits bytes to this private staging stream, not fsync per leaf.
        // Seal flushes storage once before any reader may open it.
        public override void Flush() { cancellationToken.ThrowIfCancellationRequested(); }
        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;_disposed=true;
            try { _storage.Dispose(); }
            finally { _hash.Dispose(); owner.Release(_length,true,path); }
            base.Dispose(disposing);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed && !_sealed;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
    }
}

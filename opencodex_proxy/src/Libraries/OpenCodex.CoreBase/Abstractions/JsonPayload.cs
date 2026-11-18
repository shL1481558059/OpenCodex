using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// Owns one JSON request payload for the complete request lifetime. Large source bytes can be
/// file-backed without forcing callers to allocate a second serializer-owned representation.
/// </summary>
public sealed class JsonPayload : IAsyncDisposable
{
    private ReadOnlyMemory<byte>? _utf8;
    private readonly string? _path;
    private readonly bool _deleteOnDispose;
    private Dictionary<string, object?>? _materialized;
    private bool _disposed;

    private JsonPayload(ReadOnlyMemory<byte> utf8)
    {
        _utf8 = utf8;
    }

    private JsonPayload(string path, bool deleteOnDispose)
    {
        _path = path;
        _deleteOnDispose = deleteOnDispose;
    }

    public long Length => _utf8?.Length ?? FileLength();

    public static JsonPayload FromOwnedUtf8(ReadOnlyMemory<byte> utf8) => new(utf8);

    public static JsonPayload FromFile(string path, bool deleteOnDispose = false)
        => new(Path.GetFullPath(path), deleteOnDispose);

    public async Task<Dictionary<string, object?>?> MaterializeObjectAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_materialized is not null) return _materialized;
        JsonDocument document;
        if (_utf8 is { } utf8)
        {
            document = JsonDocument.Parse(utf8);
        }
        else
        {
            await using var stream = OpenRead();
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return _materialized = (Dictionary<string, object?>)
                WebSearchPayload.FromJsonElement(document.RootElement)!;
        }
    }

    public async Task<Dictionary<string, object?>?> MaterializeObjectAndReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await MaterializeObjectAsync(cancellationToken);
        }
        finally
        {
            _utf8 = null;
        }
    }

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_utf8 is { } utf8)
        {
            if (MemoryMarshal.TryGetArray(utf8, out var segment)
                && segment.Array is { } array)
                return new MemoryStream(array, segment.Offset, segment.Count, writable: false);
            return new MemoryStream(utf8.ToArray(), writable: false);
        }
        if (_path is not null)
            return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        throw new InvalidOperationException("The in-memory JSON source has already been released.");
    }

    public async Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var source = OpenRead();
        await source.CopyToAsync(target, 16 * 1024, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _materialized = null;
        if (_deleteOnDispose && _path is not null) File.Delete(_path);
        return ValueTask.CompletedTask;
    }

    private long FileLength()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_path is not null) return new FileInfo(_path).Length;
        throw new InvalidOperationException("The in-memory JSON source has already been released.");
    }
}

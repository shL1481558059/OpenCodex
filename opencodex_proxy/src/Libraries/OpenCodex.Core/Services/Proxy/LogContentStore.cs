using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Data;

namespace OpenCodex.Core.Services.Proxy;

internal sealed class LogContentStore
{
    private const string Utf8Encoding = "utf-8";
    private readonly IOpenCodexDbContext _context;

    public LogContentStore(IOpenCodexDbContext context)
    {
        _context = context;
    }

    public void Write(
        Guid requestLogId,
        IReadOnlyDictionary<RequestLogContentSlot, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return;
        }

        var encodedBySlot = values
            .Where(pair => pair.Value is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => LogContentCodec.Encode(pair.Value!),
                EqualityComparer<RequestLogContentSlot>.Default);

        using var transaction = _context.Database.BeginTransaction();
        var updatedSlots = values.Keys.ToList();
        var replacedManifestIds = _context.RequestLogContentRefs
            .Where(reference => reference.RequestLogId == requestLogId
                && updatedSlots.Contains(reference.Slot))
            .Select(reference => reference.ManifestId)
            .Distinct()
            .ToList();
        var blocksByHash = EnsureBlocks(encodedBySlot.Values);
        var manifestsByHash = EnsureManifests(encodedBySlot.Values, blocksByHash);

        _context.RequestLogContentRefs
            .Where(reference => reference.RequestLogId == requestLogId
                && updatedSlots.Contains(reference.Slot))
            .ExecuteDelete();

        var references = encodedBySlot.Select(pair => new RequestLogContentRef
        {
            Id = Guid.NewGuid(),
            RequestLogId = requestLogId,
            Slot = pair.Key,
            ManifestId = manifestsByHash[pair.Value.Hash].Id
        });
        _context.RequestLogContentRefs.AddRange(references);
        _context.SaveChanges();
        RemoveOrphanedReplacedContent(replacedManifestIds);
        transaction.Commit();
    }

    public LogContentSnapshot Read(Guid requestLogId)
    {
        var references = _context.RequestLogContentRefs
            .AsNoTracking()
            .Where(reference => reference.RequestLogId == requestLogId)
            .ToList();
        if (references.Count == 0)
        {
            return LogContentSnapshot.Empty;
        }

        var manifestIds = references.Select(reference => reference.ManifestId).Distinct().ToList();
        var manifests = _context.LogContentManifests
            .AsNoTracking()
            .Where(manifest => manifestIds.Contains(manifest.Id))
            .ToDictionary(manifest => manifest.Id);
        if (manifests.Count != manifestIds.Count)
        {
            throw new InvalidDataException("Request log content references a missing manifest.");
        }

        var manifestChunks = _context.LogContentManifestChunks
            .AsNoTracking()
            .Where(chunk => manifestIds.Contains(chunk.ManifestId))
            .OrderBy(chunk => chunk.ManifestId)
            .ThenBy(chunk => chunk.Ordinal)
            .ToList();
        var blockIds = manifestChunks.Select(chunk => chunk.BlockId).Distinct().ToList();
        var blocks = blockIds.Count == 0
            ? new Dictionary<Guid, LogContentBlock>()
            : _context.LogContentBlocks
                .AsNoTracking()
                .Where(block => blockIds.Contains(block.Id))
                .ToDictionary(block => block.Id);
        if (blocks.Count != blockIds.Count)
        {
            throw new InvalidDataException("Log content manifest references a missing block.");
        }

        var chunksByManifest = manifestChunks
            .GroupBy(chunk => chunk.ManifestId)
            .ToDictionary(group => group.Key, group => group.OrderBy(chunk => chunk.Ordinal).ToList());
        var values = new Dictionary<RequestLogContentSlot, string>();
        foreach (var reference in references)
        {
            var manifest = manifests[reference.ManifestId];
            if (!string.Equals(manifest.Encoding, Utf8Encoding, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unsupported log manifest encoding '{manifest.Encoding}'.");
            }

            chunksByManifest.TryGetValue(manifest.Id, out var orderedReferences);
            orderedReferences ??= [];
            if (orderedReferences.Count != manifest.ChunkCount)
            {
                throw new InvalidDataException(
                    $"Log manifest {manifest.Id} expected {manifest.ChunkCount} chunks but found {orderedReferences.Count}.");
            }

            var storedChunks = orderedReferences.Select(chunkReference =>
            {
                var block = blocks[chunkReference.BlockId];
                if (block.StoredLength != block.Data.Length
                    || block.RawLength != chunkReference.RawLength
                    || block.RawLength > int.MaxValue)
                {
                    throw new InvalidDataException($"Invalid log content block metadata for {block.Id}.");
                }

                return new StoredLogContentChunk(
                    block.Sha256,
                    block.Compression,
                    checked((int)block.RawLength),
                    block.Data);
            }).ToList();
            if (manifest.RawLength > int.MaxValue)
            {
                throw new InvalidDataException($"Log manifest {manifest.Id} exceeds the supported length.");
            }

            var value = LogContentCodec.Decode(checked((int)manifest.RawLength), storedChunks);
            var actualHash = LogContentCodec.Encode(value).Hash;
            if (!string.Equals(actualHash, manifest.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Log manifest hash mismatch for {manifest.Id}.");
            }

            values[reference.Slot] = value;
        }

        return new LogContentSnapshot(values);
    }

    private Dictionary<string, LogContentBlock> EnsureBlocks(
        IEnumerable<EncodedLogContent> encodedValues)
    {
        var chunks = encodedValues
            .SelectMany(value => value.Chunks)
            .GroupBy(chunk => chunk.Hash, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (chunks.Count == 0)
        {
            return new Dictionary<string, LogContentBlock>(StringComparer.Ordinal);
        }

        foreach (var chunk in chunks)
        {
            InsertBlockIfMissing(chunk);
        }

        var hashes = chunks.Select(chunk => chunk.Hash).ToList();
        var blocks = _context.LogContentBlocks
            .Where(block => hashes.Contains(block.Sha256))
            .ToList()
            .ToDictionary(block => block.Sha256, StringComparer.Ordinal);
        if (blocks.Count != hashes.Count)
        {
            throw new InvalidDataException("One or more content-addressed log blocks could not be persisted.");
        }

        foreach (var chunk in chunks)
        {
            var block = blocks[chunk.Hash];
            if (block.RawLength != chunk.OriginalLength)
            {
                throw new InvalidDataException($"Content hash collision detected for block {chunk.Hash}.");
            }
        }

        return blocks;
    }

    private Dictionary<string, LogContentManifest> EnsureManifests(
        IEnumerable<EncodedLogContent> encodedValues,
        IReadOnlyDictionary<string, LogContentBlock> blocksByHash)
    {
        var values = encodedValues
            .GroupBy(value => value.Hash, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        foreach (var value in values)
        {
            var manifest = new LogContentManifest
            {
                Id = Guid.NewGuid(),
                Sha256 = value.Hash,
                RawLength = value.OriginalLength,
                ChunkCount = value.Chunks.Count,
                Encoding = Utf8Encoding
            };
            var inserted = InsertManifestIfMissing(manifest);
            if (!inserted)
            {
                continue;
            }

            var manifestChunks = value.Chunks.Select((chunk, ordinal) => new LogContentManifestChunk
            {
                Id = Guid.NewGuid(),
                ManifestId = manifest.Id,
                Ordinal = ordinal,
                BlockId = blocksByHash[chunk.Hash].Id,
                RawLength = chunk.OriginalLength
            });
            _context.LogContentManifestChunks.AddRange(manifestChunks);
            _context.SaveChanges();
        }

        var hashes = values.Select(value => value.Hash).ToList();
        var manifests = _context.LogContentManifests
            .Where(manifest => hashes.Contains(manifest.Sha256))
            .ToList()
            .ToDictionary(manifest => manifest.Sha256, StringComparer.Ordinal);
        if (manifests.Count != hashes.Count)
        {
            throw new InvalidDataException("One or more log content manifests could not be persisted.");
        }

        foreach (var value in values)
        {
            var manifest = manifests[value.Hash];
            if (manifest.RawLength != value.OriginalLength)
            {
                throw new InvalidDataException($"Content hash collision detected for manifest {value.Hash}.");
            }
        }

        return manifests;
    }

    private void InsertBlockIfMissing(EncodedLogContentChunk chunk)
    {
        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var sql = ProviderKind() switch
        {
            LogDatabaseProvider.Sqlite =>
                "INSERT OR IGNORE INTO \"LogContentBlocks\" "
                + "(\"Id\", \"Sha256\", \"RawLength\", \"StoredLength\", \"Compression\", \"Data\", \"CreatedAt\") "
                + "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6});",
            LogDatabaseProvider.Postgres =>
                "INSERT INTO \"LogContentBlocks\" "
                + "(\"Id\", \"Sha256\", \"RawLength\", \"StoredLength\", \"Compression\", \"Data\", \"CreatedAt\") "
                + "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}) "
                + "ON CONFLICT (\"Sha256\") DO NOTHING;",
            _ => throw new InvalidOperationException("Unsupported log database provider.")
        };
        _context.Database.ExecuteSqlRaw(
            sql,
            id,
            chunk.Hash,
            (long)chunk.OriginalLength,
            chunk.Data.Length,
            chunk.Codec,
            chunk.Data,
            createdAt);
    }

    private bool InsertManifestIfMissing(LogContentManifest manifest)
    {
        var sql = ProviderKind() switch
        {
            LogDatabaseProvider.Sqlite =>
                "INSERT OR IGNORE INTO \"LogContentManifests\" "
                + "(\"Id\", \"Sha256\", \"RawLength\", \"ChunkCount\", \"Encoding\") "
                + "VALUES ({0}, {1}, {2}, {3}, {4});",
            LogDatabaseProvider.Postgres =>
                "INSERT INTO \"LogContentManifests\" "
                + "(\"Id\", \"Sha256\", \"RawLength\", \"ChunkCount\", \"Encoding\") "
                + "VALUES ({0}, {1}, {2}, {3}, {4}) "
                + "ON CONFLICT (\"Sha256\") DO NOTHING;",
            _ => throw new InvalidOperationException("Unsupported log database provider.")
        };
        return _context.Database.ExecuteSqlRaw(
            sql,
            manifest.Id,
            manifest.Sha256,
            manifest.RawLength,
            manifest.ChunkCount,
            manifest.Encoding) == 1;
    }

    private void RemoveOrphanedReplacedContent(IReadOnlyCollection<Guid> replacedManifestIds)
    {
        if (replacedManifestIds.Count == 0)
        {
            return;
        }

        var orphanedManifestIds = _context.LogContentManifests
            .Where(manifest => replacedManifestIds.Contains(manifest.Id)
                && !_context.RequestLogContentRefs.Any(reference => reference.ManifestId == manifest.Id))
            .Select(manifest => manifest.Id)
            .ToList();
        if (orphanedManifestIds.Count == 0)
        {
            return;
        }

        var candidateBlockIds = _context.LogContentManifestChunks
            .Where(chunk => orphanedManifestIds.Contains(chunk.ManifestId))
            .Select(chunk => chunk.BlockId)
            .Distinct()
            .ToList();
        _context.LogContentManifests
            .Where(manifest => orphanedManifestIds.Contains(manifest.Id))
            .ExecuteDelete();

        if (candidateBlockIds.Count == 0)
        {
            return;
        }

        _context.LogContentBlocks
            .Where(block => candidateBlockIds.Contains(block.Id)
                && !_context.LogContentManifestChunks.Any(chunk => chunk.BlockId == block.Id))
            .ExecuteDelete();
    }

    private LogDatabaseProvider ProviderKind()
    {
        var providerName = _context.Database.ProviderName ?? string.Empty;
        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return LogDatabaseProvider.Sqlite;
        }

        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            return LogDatabaseProvider.Postgres;
        }

        throw new InvalidOperationException($"Unsupported log database provider '{providerName}'.");
    }

    private enum LogDatabaseProvider
    {
        Sqlite,
        Postgres
    }
}

internal sealed class LogContentSnapshot
{
    public static LogContentSnapshot Empty { get; } = new(
        new Dictionary<RequestLogContentSlot, string>());

    private readonly IReadOnlyDictionary<RequestLogContentSlot, string> _values;

    public LogContentSnapshot(IReadOnlyDictionary<RequestLogContentSlot, string> values)
    {
        _values = values;
    }

    public string? Get(RequestLogContentSlot slot)
    {
        return _values.TryGetValue(slot, out var value) ? value : null;
    }
}

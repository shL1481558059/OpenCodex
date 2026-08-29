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

    private Dictionary<string, BlockRef> EnsureBlocks(
        IEnumerable<EncodedLogContent> encodedValues)
    {
        var chunks = encodedValues
            .SelectMany(value => value.Chunks)
            .GroupBy(chunk => chunk.Hash, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (chunks.Count == 0)
        {
            return new Dictionary<string, BlockRef>(StringComparer.Ordinal);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var hashes = chunks.Select(chunk => chunk.Hash).ToList();
            var existing = _context.LogContentBlocks
                .AsNoTracking()
                .Where(block => hashes.Contains(block.Sha256))
                .Select(block => new BlockRef(block.Id, block.Sha256, block.RawLength))
                .ToDictionary(block => block.Sha256, StringComparer.Ordinal);
            var missing = chunks.Where(chunk => !existing.ContainsKey(chunk.Hash)).ToList();
            if (missing.Count == 0)
            {
                return ValidateBlocks(existing, chunks);
            }

            CreateSavepoint("log_content_blocks");
            var missingBlocks = missing.Select(chunk => new LogContentBlock
            {
                Id = Guid.NewGuid(),
                Sha256 = chunk.Hash,
                RawLength = chunk.OriginalLength,
                StoredLength = chunk.Data.Length,
                Compression = chunk.Codec,
                Data = chunk.Data,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
            }).ToList();
            _context.LogContentBlocks.AddRange(missingBlocks);
            try
            {
                _context.SaveChanges();
                foreach (var block in missingBlocks)
                {
                    existing[block.Sha256] = new BlockRef(block.Id, block.Sha256, block.RawLength);
                }

                return ValidateBlocks(existing, chunks);
            }
            catch (DbUpdateException)
            {
                // 并发下另一事务可能已插入同一 Sha256。回滚到 savepoint 清掉本次
                // 未提交的插入,Detach 新增实体后重查,以实际落库的行作为引用依据。
                RollbackToSavepoint("log_content_blocks");
                DetachEntities(missingBlocks);
            }
        }

        throw new InvalidDataException("One or more content-addressed log blocks could not be persisted.");
    }

    private Dictionary<string, LogContentManifest> EnsureManifests(
        IEnumerable<EncodedLogContent> encodedValues,
        IReadOnlyDictionary<string, BlockRef> blocksByHash)
    {
        var values = encodedValues
            .GroupBy(value => value.Hash, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = _context.LogContentManifests
                .AsNoTracking()
                .Where(manifest => values.Select(value => value.Hash).Contains(manifest.Sha256))
                .ToDictionary(manifest => manifest.Sha256, StringComparer.Ordinal);
            var missing = values.Where(value => !existing.ContainsKey(value.Hash)).ToList();
            if (missing.Count == 0)
            {
                return ValidateManifests(existing, values);
            }

            CreateSavepoint("log_content_manifests");
            var addedManifests = missing.Select(value => new LogContentManifest
            {
                Id = Guid.NewGuid(),
                Sha256 = value.Hash,
                RawLength = value.OriginalLength,
                ChunkCount = value.Chunks.Count,
                Encoding = Utf8Encoding
            }).ToList();
            _context.LogContentManifests.AddRange(addedManifests);
            foreach (var manifest in addedManifests)
            {
                var value = missing.First(item => item.Hash == manifest.Sha256);
                var manifestChunks = value.Chunks.Select((chunk, ordinal) => new LogContentManifestChunk
                {
                    Id = Guid.NewGuid(),
                    ManifestId = manifest.Id,
                    Ordinal = ordinal,
                    BlockId = blocksByHash[chunk.Hash].Id,
                    RawLength = chunk.OriginalLength
                });
                _context.LogContentManifestChunks.AddRange(manifestChunks);
            }

            try
            {
                _context.SaveChanges();
                foreach (var manifest in addedManifests)
                {
                    existing[manifest.Sha256] = manifest;
                }

                return ValidateManifests(existing, values);
            }
            catch (DbUpdateException)
            {
                // 并发下另一事务可能已插入同一 Sha256 manifest。回滚到 savepoint,
                // Detach 本请求新增的 manifest 与 chunks 后重查。
                RollbackToSavepoint("log_content_manifests");
                var addedChunkIds = addedManifests.Select(manifest => manifest.Id).ToHashSet();
                DetachEntities(addedManifests);
                foreach (var chunk in _context.LogContentManifestChunks.Local
                             .Where(chunk => addedChunkIds.Contains(chunk.ManifestId))
                             .ToList())
                {
                    _context.Entry(chunk).State = EntityState.Detached;
                }
            }
        }

        throw new InvalidDataException("One or more log content manifests could not be persisted.");
    }

    private static Dictionary<string, BlockRef> ValidateBlocks(
        IReadOnlyDictionary<string, BlockRef> existing,
        IReadOnlyList<EncodedLogContentChunk> chunks)
    {
        if (existing.Count != chunks.Count)
        {
            throw new InvalidDataException("One or more content-addressed log blocks could not be persisted.");
        }

        foreach (var chunk in chunks)
        {
            var block = existing[chunk.Hash];
            if (block.RawLength != chunk.OriginalLength)
            {
                throw new InvalidDataException($"Content hash collision detected for block {chunk.Hash}.");
            }
        }

        return existing.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private readonly record struct BlockRef(Guid Id, string Sha256, long RawLength);

    private static Dictionary<string, LogContentManifest> ValidateManifests(
        IReadOnlyDictionary<string, LogContentManifest> existing,
        IReadOnlyList<EncodedLogContent> values)
    {
        if (existing.Count != values.Count)
        {
            throw new InvalidDataException("One or more log content manifests could not be persisted.");
        }

        foreach (var value in values)
        {
            var manifest = existing[value.Hash];
            if (manifest.RawLength != value.OriginalLength)
            {
                throw new InvalidDataException($"Content hash collision detected for manifest {value.Hash}.");
            }
        }

        return existing.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private void CreateSavepoint(string name)
    {
        // 必须要在执行可能冲突的 SaveChanges 之前建 savepoint,冲突发生后才能回滚。
        _context.Database.CurrentTransaction?.CreateSavepoint(name);
    }

    private void RollbackToSavepoint(string name)
    {
        _context.Database.CurrentTransaction?.RollbackToSavepoint(name);
    }

    private void DetachEntities<TEntity>(IEnumerable<TEntity> entities)
        where TEntity : class
    {
        foreach (var entity in entities)
        {
            _context.Entry(entity).State = EntityState.Detached;
        }
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

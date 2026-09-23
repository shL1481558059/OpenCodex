using Microsoft.EntityFrameworkCore;
using System.Text;
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
        WriteUtf8(
            requestLogId,
            values.ToDictionary(
                pair => pair.Key,
                pair => pair.Value is null ? null : Encoding.UTF8.GetBytes(pair.Value),
                EqualityComparer<RequestLogContentSlot>.Default));
    }

    /// <summary>
    /// 与 <see cref="Write"/> 等价,但直接接收 UTF-8 字节。
    /// </summary>
    /// <remarks>
    /// 已经以 UTF-8 形式存在的内容(例如用 <see cref="Utf8JsonWriter"/> 直出的流日志)
    /// 走这条路径可以省掉一次"字节 → 字符串 → 字节"的往返转换。
    /// </remarks>
    public void WriteUtf8(
        Guid requestLogId,
        IReadOnlyDictionary<RequestLogContentSlot, byte[]?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return;
        }

        // 先只分块 + 算哈希,不压缩;等查重结果出来后再压缩缺失的块。
        var plansBySlot = values
            .Where(pair => pair.Value is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => LogContentCodec.Plan(pair.Value!),
                EqualityComparer<RequestLogContentSlot>.Default);
        var plans = plansBySlot.Values.ToList();

        using var transaction = _context.Database.BeginTransaction();
        var updatedSlots = values.Keys.ToList();
        var replacedManifestIds = _context.RequestLogContentRefs
            .Where(reference => reference.RequestLogId == requestLogId
                && updatedSlots.Contains(reference.Slot))
            .Select(reference => reference.ManifestId)
            .Distinct()
            .ToList();
        var blocksByHash = EnsureBlocks(plans);
        var manifestsByHash = EnsureManifests(plans, blocksByHash);

        _context.RequestLogContentRefs
            .Where(reference => reference.RequestLogId == requestLogId
                && updatedSlots.Contains(reference.Slot))
            .ExecuteDelete();

        var references = plansBySlot.Select(pair => new RequestLogContentRef
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

            var value = LogContentCodec.Decode(
                checked((int)manifest.RawLength),
                storedChunks,
                out var actualHash);
            if (!string.Equals(actualHash, manifest.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Log manifest hash mismatch for {manifest.Id}.");
            }

            values[reference.Slot] = value;
        }

        return new LogContentSnapshot(values);
    }

    private Dictionary<string, BlockRef> EnsureBlocks(IReadOnlyList<LogContentPlan> plans)
    {
        var chunkPlansByHash = plans
            .SelectMany(plan => plan.Chunks)
            .GroupBy(chunk => chunk.Hash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (chunkPlansByHash.Count == 0)
        {
            return new Dictionary<string, BlockRef>(StringComparer.Ordinal);
        }

        var hashes = chunkPlansByHash.Keys.ToList();
        var existing = QueryBlocks(hashes);

        // 只压缩库中尚不存在的块。同一会话反复发送时绝大多数块可以命中,
        // 这一步省掉的正是原先"先全量压缩、再查重丢弃"的重复 Brotli 开销。
        var compressedByHash = new Dictionary<string, EncodedLogContentChunk>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            foreach (var chunk in plan.Chunks)
            {
                if (existing.ContainsKey(chunk.Hash) || compressedByHash.ContainsKey(chunk.Hash))
                {
                    continue;
                }

                compressedByHash[chunk.Hash] = LogContentCodec.Compress(plan.Source, chunk);
            }
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var missingBlocks = compressedByHash
                .Where(pair => !existing.ContainsKey(pair.Key))
                .Select(pair => new LogContentBlock
                {
                    Id = Guid.NewGuid(),
                    Sha256 = pair.Key,
                    RawLength = pair.Value.OriginalLength,
                    StoredLength = pair.Value.Data.Length,
                    Compression = pair.Value.Codec,
                    Data = pair.Value.Data,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
                })
                .ToList();
            if (missingBlocks.Count == 0)
            {
                return ValidateBlocks(existing, chunkPlansByHash.Values);
            }

            CreateSavepoint("log_content_blocks");
            _context.LogContentBlocks.AddRange(missingBlocks);
            try
            {
                _context.SaveChanges();
                foreach (var block in missingBlocks)
                {
                    existing[block.Sha256] = new BlockRef(block.Id, block.Sha256, block.RawLength);
                }

                return ValidateBlocks(existing, chunkPlansByHash.Values);
            }
            catch (DbUpdateException)
            {
                // 并发下另一事务可能已插入同一 Sha256。回滚到 savepoint 清掉本次
                // 未提交的插入,Detach 新增实体后重查,以实际落库的行作为引用依据。
                RollbackToSavepoint("log_content_blocks");
                DetachEntities(missingBlocks);
                existing = QueryBlocks(hashes);
            }
        }

        throw new InvalidDataException("One or more content-addressed log blocks could not be persisted.");
    }

    private Dictionary<string, BlockRef> QueryBlocks(IReadOnlyList<string> hashes)
    {
        return _context.LogContentBlocks
            .AsNoTracking()
            .Where(block => hashes.Contains(block.Sha256))
            .Select(block => new BlockRef(block.Id, block.Sha256, block.RawLength))
            .ToDictionary(block => block.Sha256, StringComparer.Ordinal);
    }

    private Dictionary<string, LogContentManifest> EnsureManifests(
        IReadOnlyList<LogContentPlan> plans,
        IReadOnlyDictionary<string, BlockRef> blocksByHash)
    {
        var values = plans
            .GroupBy(value => value.Hash, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var hashes = values.Select(value => value.Hash).ToList();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = _context.LogContentManifests
                .AsNoTracking()
                .Where(manifest => hashes.Contains(manifest.Sha256))
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
                    RawLength = chunk.Length
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
        IEnumerable<LogChunkPlan> chunks)
    {
        var chunkList = chunks as IReadOnlyCollection<LogChunkPlan> ?? chunks.ToList();
        if (existing.Count != chunkList.Count)
        {
            throw new InvalidDataException("One or more content-addressed log blocks could not be persisted.");
        }

        foreach (var chunk in chunkList)
        {
            var block = existing[chunk.Hash];
            if (block.RawLength != chunk.Length)
            {
                throw new InvalidDataException($"Content hash collision detected for block {chunk.Hash}.");
            }
        }

        return existing.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private readonly record struct BlockRef(Guid Id, string Sha256, long RawLength);

    private static Dictionary<string, LogContentManifest> ValidateManifests(
        IReadOnlyDictionary<string, LogContentManifest> existing,
        IReadOnlyList<LogContentPlan> values)
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

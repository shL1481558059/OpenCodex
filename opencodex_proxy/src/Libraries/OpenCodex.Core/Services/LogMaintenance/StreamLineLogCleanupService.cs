using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.DTOs.LogMaintenance;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services.LogMaintenance;

/// <summary>
/// 清理历史流日志（内容槽位 8）残留数据。
/// </summary>
/// <remarks>
/// 流日志槽位已从 <see cref="RequestLogContentSlot"/> 移除，但历史库中仍存在
/// 数值为 8 的引用。物理块为多个请求共享，因此只有在确认没有任何清单引用后才能删除。
/// </remarks>
public sealed class StreamLineLogCleanupService : IStreamLineLogCleanupService
{
    /// <summary>
    /// 历史流日志使用的内容槽位数值。
    /// </summary>
    private const short LegacyStreamLinesSlotValue = 8;

    private static readonly RequestLogContentSlot LegacyStreamLinesSlot =
        (RequestLogContentSlot)LegacyStreamLinesSlotValue;

    private readonly IOpenCodexDbContext _context;

    public StreamLineLogCleanupService(IOpenCodexDbContext context)
    {
        _context = context;
    }

    public async Task<LegacyStreamLineLogCleanupResult> ExecuteAsync(
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        var candidateManifestIds = await _context.RequestLogContentRefs
            .Where(reference => reference.Slot == LegacyStreamLinesSlot)
            .Select(reference => reference.ManifestId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var deletedRefs = await _context.RequestLogContentRefs
            .Where(reference => reference.Slot == LegacyStreamLinesSlot)
            .ExecuteDeleteAsync(cancellationToken);

        var orphanedManifestIds = await _context.LogContentManifests
            .Where(manifest => candidateManifestIds.Contains(manifest.Id)
                && !_context.RequestLogContentRefs.Any(reference => reference.ManifestId == manifest.Id))
            .Select(manifest => manifest.Id)
            .ToListAsync(cancellationToken);

        var candidateBlockIds = await _context.LogContentManifestChunks
            .Where(chunk => orphanedManifestIds.Contains(chunk.ManifestId))
            .Select(chunk => chunk.BlockId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var deletedChunks = await _context.LogContentManifestChunks
            .Where(chunk => orphanedManifestIds.Contains(chunk.ManifestId))
            .ExecuteDeleteAsync(cancellationToken);

        var deletedManifests = await _context.LogContentManifests
            .Where(manifest => orphanedManifestIds.Contains(manifest.Id))
            .ExecuteDeleteAsync(cancellationToken);

        var orphanedBlocks = await _context.LogContentBlocks
            .Where(block => candidateBlockIds.Contains(block.Id)
                && !_context.LogContentManifestChunks.Any(chunk => chunk.BlockId == block.Id))
            .Select(block => new { block.Id, block.RawLength, block.StoredLength })
            .ToListAsync(cancellationToken);

        var orphanedBlockIds = orphanedBlocks.Select(item => item.Id).ToList();
        var deletedBlocks = orphanedBlockIds.Count == 0
            ? 0
            : await _context.LogContentBlocks
                .Where(block => orphanedBlockIds.Contains(block.Id))
                .ExecuteDeleteAsync(cancellationToken);

        if (dryRun)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        else
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new LegacyStreamLineLogCleanupResult(
            dryRun,
            deletedRefs,
            deletedManifests,
            deletedChunks,
            deletedBlocks,
            orphanedBlocks.Sum(item => item.RawLength),
            orphanedBlocks.Sum(item => item.StoredLength));
    }
}

using OpenCodex.CoreBase.DTOs.LogMaintenance;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义历史流日志（内容槽位 8）的一次性清理服务。
/// </summary>
public interface IStreamLineLogCleanupService
{
    /// <summary>
    /// 删除历史流日志引用、清单与不再被引用的物理块。
    /// </summary>
    /// <param name="dryRun">为 <see langword="true"/> 时只统计并在事务结束时回滚。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>清理结果。</returns>
    Task<LegacyStreamLineLogCleanupResult> ExecuteAsync(
        bool dryRun,
        CancellationToken cancellationToken = default);
}

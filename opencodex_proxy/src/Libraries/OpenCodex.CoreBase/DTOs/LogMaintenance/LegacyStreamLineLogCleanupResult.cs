namespace OpenCodex.CoreBase.DTOs.LogMaintenance;

/// <summary>
/// 表示历史流日志（内容槽位 8）清理结果。
/// </summary>
public sealed class LegacyStreamLineLogCleanupResult(
    bool dryRun,
    int contentRefs,
    int manifests,
    int manifestChunks,
    int blocks,
    long rawBytes,
    long storedBytes)
{
    /// <summary>
    /// 获取是否为只统计不删除的预演。
    /// </summary>
    public bool DryRun { get; } = dryRun;

    /// <summary>
    /// 获取被删除或将被删除的日志内容引用数量。
    /// </summary>
    public int ContentRefs { get; } = contentRefs;

    /// <summary>
    /// 获取被删除或将被删除的正文清单数量。
    /// </summary>
    public int Manifests { get; } = manifests;

    /// <summary>
    /// 获取被删除或将被删除的清单分块数量。
    /// </summary>
    public int ManifestChunks { get; } = manifestChunks;

    /// <summary>
    /// 获取被删除或将被删除的物理块数量。
    /// </summary>
    public int Blocks { get; } = blocks;

    /// <summary>
    /// 获取被删除或将被删除的物理块原始字节数。
    /// </summary>
    public long RawBytes { get; } = rawBytes;

    /// <summary>
    /// 获取被删除或将被删除的物理块存储字节数。
    /// </summary>
    public long StoredBytes { get; } = storedBytes;
}

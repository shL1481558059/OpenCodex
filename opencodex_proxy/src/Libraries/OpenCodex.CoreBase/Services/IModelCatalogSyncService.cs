using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

/// <summary>
 /// 提供模型目录远端同步能力。
 /// </summary>
public interface IModelCatalogSyncService
{
    /// <summary>
    /// 从远端拉取模型目录并按指定模式预检或写入。
    /// </summary>
    /// <param name="mode">同步模式: incremental 或 overwrite。</param>
    /// <param name="dryRun">true 表示仅预检不写库。</param>
    /// <returns>导入结果。</returns>
    Task<ApiOpResult<ModelCatalogImportResult>> SyncAsync(string mode, bool dryRun);
}

/// <summary>
 /// 模型目录同步的 HttpClient 抽象,用于依赖注入与测试 stub。
 /// </summary>
public interface IModelCatalogSyncClient
{
    /// <summary>
    /// 拉取远端 JSON 并返回反序列化后的文档。
    /// </summary>
    /// <param name="url">远端地址。</param>
    /// <returns>反序列化后的模型目录文档;失败时抛出异常。</returns>
    Task<ModelCatalogTransferDocument> FetchAsync(string url);
}

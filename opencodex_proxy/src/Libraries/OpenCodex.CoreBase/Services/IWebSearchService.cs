using OpenCodex.CoreBase.DTOs.WebSearch;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义后台联网搜索配置服务。
/// </summary>
public interface IWebSearchService
{
    /// <summary>
    /// 读取联网搜索配置。
    /// </summary>
    /// <returns>联网搜索配置结果。</returns>
    ApiOpResult<WebSearchConfigResponse> ReadConfig();

    /// <summary>
    /// 保存联网搜索配置。
    /// </summary>
    /// <param name="body">联网搜索配置请求内容。</param>
    /// <returns>保存后的联网搜索配置结果。</returns>
    ApiOpResult<WebSearchConfigResponse> SaveConfig(
        IReadOnlyDictionary<string, object?> body);

    /// <summary>
    /// 测试指定联网搜索密钥。
    /// </summary>
    /// <param name="keyId">密钥标识。</param>
    /// <param name="query">测试搜索词。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>密钥测试结果。</returns>
    Task<ApiOpResult<WebSearchTestKeyResponsePayload>> TestKeyAsync(
        long keyId,
        string query,
        CancellationToken cancellationToken);
}

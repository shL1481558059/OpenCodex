using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.ApiKeys;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义后台访问密钥管理服务。
/// </summary>
public interface IApiKeyService
{
    /// <summary>
    /// 读取访问密钥列表。
    /// </summary>
    /// <param name="requestedOwnerUsername">请求查看的拥有者用户名；为空时按当前用户上下文处理。</param>
    /// <returns>访问密钥列表结果。</returns>
    ApiOpResult<ApiKeysResponse> ListKeys(
        string? requestedOwnerUsername);

    /// <summary>
    /// 创建新的访问密钥。
    /// </summary>
    /// <param name="command">创建访问密钥命令。</param>
    /// <returns>创建后的访问密钥结果。</returns>
    ApiOpResult<ApiKeyResponsePayload> CreateKey(
        ApiKeyCreateCommand command);

    /// <summary>
    /// 更新指定访问密钥。
    /// </summary>
    /// <param name="keyId">访问密钥标识。</param>
    /// <param name="command">更新访问密钥命令。</param>
    /// <returns>更新后的访问密钥结果。</returns>
    ApiOpResult<ApiKeyResponsePayload> UpdateKey(
        long keyId,
        ApiKeyUpdateCommand command);

    /// <summary>
    /// 删除指定访问密钥。
    /// </summary>
    /// <param name="keyId">访问密钥标识。</param>
    /// <returns>删除操作结果。</returns>
    ApiOpResult<DeleteApiKeyResponse> DeleteKey(
        long keyId);
}

using OpenCodex.CoreBase.DTOs.WebSearch;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.Api.Services;

/// <summary>
/// 联网搜索管理服务（测试密钥等管理端操作）。
/// </summary>
public interface IWebSearchAdminService
{
    Task<ApiOpResult<WebSearchTestKeyResponsePayload>> TestKeyAsync(
        WebSearchTestKeyRequest request,
        CancellationToken cancellationToken);
}

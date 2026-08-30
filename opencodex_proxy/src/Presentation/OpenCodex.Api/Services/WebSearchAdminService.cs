using OpenCodex.CoreBase.DTOs.WebSearch;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Services;

/// <summary>
/// 联网搜索管理实现：测试密钥。
/// </summary>
public sealed class WebSearchAdminService : IWebSearchAdminService
{
    private readonly IWebSearchService _webSearch;

    public WebSearchAdminService(IWebSearchService webSearch)
    {
        _webSearch = webSearch;
    }

    public Task<ApiOpResult<WebSearchTestKeyResponsePayload>> TestKeyAsync(
        WebSearchTestKeyRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Id is null)
        {
            return Task.FromResult(
                ApiOpResult<WebSearchTestKeyResponsePayload>.Fail(400, "id is required"));
        }

        return _webSearch.TestKeyAsync(
            request.Id.Value,
            request.EffectiveQuery(),
            cancellationToken);
    }
}

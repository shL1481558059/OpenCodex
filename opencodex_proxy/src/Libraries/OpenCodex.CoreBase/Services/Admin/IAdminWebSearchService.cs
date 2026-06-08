using OpenCodex.CoreBase.DTOs.AdminWebSearch;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services.Admin;

public interface IAdminWebSearchService
{
    ApiResult<WebSearchConfigResponse> ReadConfig();

    ApiResult<WebSearchConfigResponse> SaveConfig(
        IReadOnlyDictionary<string, object?> body);

    Task<ApiResult<WebSearchTestKeyResponsePayload>> TestKeyAsync(
        long keyId,
        string query,
        CancellationToken cancellationToken);
}

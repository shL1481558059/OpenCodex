using OpenCodex.Api.Domain;
using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public interface IAdminWebSearchService
{
    ServiceResult<WebSearchConfigRecord> ReadConfig();

    ServiceResult<WebSearchConfigRecord> SaveConfig(
        IReadOnlyDictionary<string, object?> body);

    ServiceResult<TavilyKeyRecord> ReserveTestKey(long keyId);

    Task<ServiceResult<AdminWebSearchTestResult>> TestKeyAsync(
        long keyId,
        string query,
        CancellationToken cancellationToken);
}

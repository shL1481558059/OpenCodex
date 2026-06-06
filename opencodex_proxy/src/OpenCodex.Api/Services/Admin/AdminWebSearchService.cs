using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public sealed class AdminWebSearchService : IAdminWebSearchService
{
    private const string KeyUnavailableMessage = "Web Search key is unavailable or has reached its usage limit";

    private readonly IAdminWebSearchRepository _webSearch;
    private readonly IWebSearchClient _webSearchClient;

    public AdminWebSearchService(
        IAdminWebSearchRepository webSearch,
        IWebSearchClient webSearchClient)
    {
        _webSearch = webSearch;
        _webSearchClient = webSearchClient;
    }

    public ServiceResult<WebSearchConfigRecord> ReadConfig()
    {
        return ServiceResult.Success(_webSearch.ReadWebSearchConfig());
    }

    public ServiceResult<WebSearchConfigRecord> SaveConfig(
        IReadOnlyDictionary<string, object?> body)
    {
        try
        {
            return ServiceResult.Success(_webSearch.ReplaceWebSearchConfig(body));
        }
        catch (ArgumentException exception)
        {
            return ServiceResult.Fail<WebSearchConfigRecord>(
                AdminWebSearchErrorCodes.Validation,
                exception.Message);
        }
    }

    public ServiceResult<TavilyKeyRecord> ReserveTestKey(long keyId)
    {
        var key = _webSearch.ReserveTavilyKeyById(keyId);
        return key is null
            ? ServiceResult.Fail<TavilyKeyRecord>(
                AdminWebSearchErrorCodes.KeyUnavailable,
                KeyUnavailableMessage)
            : ServiceResult.Success(key);
    }

    public async Task<ServiceResult<AdminWebSearchTestResult>> TestKeyAsync(
        long keyId,
        string query,
        CancellationToken cancellationToken)
    {
        var reserved = ReserveTestKey(keyId);
        if (!reserved.Succeeded || reserved.Data is null)
        {
            return ServiceResult.Fail<AdminWebSearchTestResult>(
                reserved.Code,
                reserved.Message);
        }

        var result = await _webSearchClient.SearchAsync(
            new WebSearchProviderKey(reserved.Data.Provider, reserved.Data.Key),
            query,
            cancellationToken);
        return ServiceResult.Success(new AdminWebSearchTestResult(
            reserved.Data,
            result,
            _webSearch.ReadWebSearchConfig()));
    }
}

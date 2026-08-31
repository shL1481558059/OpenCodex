using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

public interface IProxySettingsService
{
    bool GetBool(string key, bool fallback);

    decimal GetDecimal(string key, decimal fallback);

    Task<ApiOpResult<Dictionary<string, string>>> GetAllAsync();

    Task<ApiOpResult> SetAsync(string key, string value);
}

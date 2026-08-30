using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class ProxySettingsService : IProxySettingsService
{
    private readonly IRepository<ProxySetting> _repository;

    public ProxySettingsService(IRepository<ProxySetting> repository)
    {
        _repository = repository;
    }

    public bool GetBool(string key, bool fallback)
    {
        var setting = _repository.TableNoTracking
            .FirstOrDefault(item => item.Key == key);
        if (setting is null)
        {
            return fallback;
        }

        return bool.TryParse(setting.Value, out var value) ? value : fallback;
    }

    public Task<ApiOpResult<Dictionary<string, string>>> GetAllAsync()
    {
        var settings = _repository.TableNoTracking
            .ToDictionary(item => item.Key, item => item.Value);
        return Task.FromResult(ApiOpResult<Dictionary<string, string>>.Succeed(settings));
    }

    public async Task<ApiOpResult> SetAsync(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return ApiOpResult.Fail(400, "key must not be empty");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var existing = _repository.Table
            .FirstOrDefault(item => item.Key == key);
        if (existing is null)
        {
            await _repository.InsertAsync(new ProxySetting
            {
                Key = key,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = now;
            await _repository.UpdateAsync(existing);
        }

        return ApiOpResult.Succeed();
    }
}

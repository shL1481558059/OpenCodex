using OpenCodex.Api.Configuration;
using OpenCodex.CoreBase.DTOs.SystemSettings;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Services;

/// <summary>
/// 系统设置与代理设置读写实现。
/// </summary>
public sealed class SystemSettingsControllerService : ISystemSettingsControllerService
{
    private readonly IDesktopSystemSettingsStore _settings;
    private readonly IProxySettingsService _proxySettings;

    public SystemSettingsControllerService(
        IDesktopSystemSettingsStore settings,
        IProxySettingsService proxySettings)
    {
        _settings = settings;
        _proxySettings = proxySettings;
    }

    public ApiOpResult<SystemSettingsResponse> ReadSettings()
        => ApiOpResult<SystemSettingsResponse>.Succeed(_settings.Get());

    public ApiOpResult<SystemSettingsResponse> UpdateSettings(SystemSettingsUpdateRequest request)
    {
        try
        {
            var settings = _settings.Save(_settings.Normalize(request));
            return ApiOpResult<SystemSettingsResponse>.Succeed(settings);
        }
        catch (ArgumentException exception)
        {
            return ApiOpResult<SystemSettingsResponse>.Fail(400, exception.Message);
        }
    }

    public async Task<ApiOpResult<ProxySettingsResponse>> ReadProxySettingsAsync()
    {
        var settings = await _proxySettings.GetAllAsync();
        return ApiOpResult<ProxySettingsResponse>.Succeed(new ProxySettingsResponse
        {
            Settings = settings.Payload ?? new Dictionary<string, string>()
        });
    }

    public async Task<ApiOpResult<ProxySettingsResponse>> UpdateProxySettingsAsync(
        ProxySettingsUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return ApiOpResult<ProxySettingsResponse>.Fail(400, "key must not be empty");
        }

        var result = await _proxySettings.SetAsync(request.Key, request.Value ?? string.Empty);
        if (!result.Succeeded)
        {
            return ApiOpResult<ProxySettingsResponse>.Fail(
                result.Code,
                result.Description ?? "failed to update proxy settings");
        }

        return await ReadProxySettingsAsync();
    }
}

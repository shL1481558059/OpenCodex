using OpenCodex.CoreBase.DTOs.SystemSettings;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.Api.Services;

/// <summary>
/// 系统设置与代理设置读写服务。
/// </summary>
public interface ISystemSettingsControllerService
{
    ApiOpResult<SystemSettingsResponse> ReadSettings();

    ApiOpResult<SystemSettingsResponse> UpdateSettings(SystemSettingsUpdateRequest request);

    Task<ApiOpResult<ProxySettingsResponse>> ReadProxySettingsAsync();

    Task<ApiOpResult<ProxySettingsResponse>> UpdateProxySettingsAsync(ProxySettingsUpdateRequest request);
}

using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Configuration;
using OpenCodex.CoreBase.DTOs.SystemSettings;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class SystemSettingsController : AuthenticatedApiControllerBase
{
    private readonly IDesktopSystemSettingsStore _settings;
    private readonly IVisionTransferSettingsService _visionTransfer;
    private readonly IProxySettingsService _proxySettings;

    public SystemSettingsController(
        IWorkContext workContext,
        IDesktopSystemSettingsStore settings,
        IVisionTransferSettingsService visionTransfer,
        IProxySettingsService proxySettings)
        : base(workContext)
    {
        _settings = settings;
        _visionTransfer = visionTransfer;
        _proxySettings = proxySettings;
    }

    [HttpGet("/system-settings")]
    public IActionResult GetSettings()
    {
        RequireSuperadmin();
        return Api(ApiOpResult<SystemSettingsResponse>.Succeed(_settings.Get()));
    }

    [HttpPut("/system-settings")]
    public IActionResult UpdateSettings(SystemSettingsUpdateRequest request)
    {
        RequireSuperadmin();
        try
        {
            var settings = _settings.Save(_settings.Normalize(request));
            return Api(ApiOpResult<SystemSettingsResponse>.Succeed(settings));
        }
        catch (ArgumentException exception)
        {
            return Api(ApiOpResult<SystemSettingsResponse>.Fail(400, exception.Message));
        }
    }

    [HttpGet("/system-settings/proxy-settings")]
    public async Task<IActionResult> GetProxySettings()
    {
        RequireSuperadmin();
        var settings = await _proxySettings.GetAllAsync();
        return Api(ApiOpResult<ProxySettingsResponse>.Succeed(new ProxySettingsResponse
        {
            Settings = settings.Payload ?? new Dictionary<string, string>()
        }));
    }

    [HttpPut("/system-settings/proxy-settings")]
    public async Task<IActionResult> UpdateProxySettings(ProxySettingsUpdateRequest request)
    {
        RequireSuperadmin();
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return Api(ApiOpResult<ProxySettingsResponse>.Fail(400, "key must not be empty"));
        }

        var result = await _proxySettings.SetAsync(request.Key, request.Value ?? string.Empty);
        if (!result.Succeeded)
        {
            return Api(result);
        }

        var settings = await _proxySettings.GetAllAsync();
        return Api(ApiOpResult<ProxySettingsResponse>.Succeed(new ProxySettingsResponse
        {
            Settings = settings.Payload ?? new Dictionary<string, string>()
        }));
    }

    // 以下四个端点是 per-owner 配置,普通 user 也可以维护自己那一份。
    // owner 归属收敛在服务层完成:非 superadmin 传别人的 owner_username 会被强制改写为自己。

    [HttpGet("/system-settings/vision-transfer")]
    public IActionResult GetVisionTransfer([FromQuery(Name = "owner_username")] string? ownerUsername)
    {
        RequireUser();
        return Api(_visionTransfer.Read(ownerUsername));
    }

    [HttpGet("/system-settings/vision-transfer/candidates")]
    public IActionResult ListVisionTransferCandidates([FromQuery(Name = "owner_username")] string? ownerUsername)
    {
        RequireUser();
        return Api(_visionTransfer.ListCandidates(ownerUsername));
    }

    [HttpPut("/system-settings/vision-transfer")]
    public IActionResult SaveVisionTransfer(VisionTransferSettingsUpdateRequest request)
    {
        RequireUser();
        return Api(_visionTransfer.Save(request));
    }

    [HttpDelete("/system-settings/vision-transfer")]
    public IActionResult DeleteVisionTransfer([FromQuery(Name = "owner_username")] string? ownerUsername)
    {
        RequireUser();
        return Api(_visionTransfer.Delete(ownerUsername));
    }
}

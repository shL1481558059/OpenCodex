using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using OpenCodex.Api.Configuration;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Services;

/// <summary>
/// 首次初始化实现：读取状态、规范化设置、初始化超级管理员并写入会话。
/// </summary>
public sealed class SetupControllerService : ISetupControllerService
{
    private readonly IAuthService _authService;
    private readonly IDesktopSystemSettingsStore _systemSettings;
    private readonly IOptionsMonitor<CookieAuthenticationOptions> _cookieOptions;

    public SetupControllerService(
        IAuthService authService,
        IDesktopSystemSettingsStore systemSettings,
        IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
    {
        _authService = authService;
        _systemSettings = systemSettings;
        _cookieOptions = cookieOptions;
    }

    public ApiOpResult<SetupStatusResponse> Status()
    {
        var state = _authService.GetSetupState();
        if (!state.Succeeded || state.Payload is null)
        {
            return ApiOpResult<SetupStatusResponse>.Fail(
                state.Code,
                state.Description ?? "setup state unavailable");
        }

        return ApiOpResult<SetupStatusResponse>.Succeed(
            SetupStatusResponse.From(state.Payload, _systemSettings.Get()));
    }

    public async Task<ApiOpResult<SetupCompleteResponse>> SetupAsync(SetupRequest request)
    {
        DesktopSystemSettingsDraft settingsDraft;
        try
        {
            settingsDraft = _systemSettings.Normalize(request.SystemSettings);
        }
        catch (ArgumentException exception)
        {
            return ApiOpResult<SetupCompleteResponse>.Fail(400, exception.Message);
        }

        var result = _authService.Initialize(request.Username, request.Password);
        if (!result.Succeeded || result.Payload?.User is null)
        {
            return ApiOpResult<SetupCompleteResponse>.Fail(
                result.Code,
                result.Description ?? "setup failed");
        }

        var savedSettings = _systemSettings.Save(settingsDraft);
        await _authService.SetUserAsync(
            new SessionUser(
                result.Payload.User.UserId,
                result.Payload.User.Username,
                result.Payload.User.Role,
                result.Payload.User.Enabled),
            _cookieOptions.Get(IAuthService.AuthenticationScheme).ExpireTimeSpan);

        return ApiOpResult<SetupCompleteResponse>.Succeed(
            new SetupCompleteResponse(result.Payload, savedSettings));
    }
}

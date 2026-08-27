using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenCodex.Api.Configuration;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class SetupController : ApiControllerBase
{
    private readonly IAuthService _authService;
    private readonly IDesktopSystemSettingsStore _systemSettings;
    private readonly IOptionsMonitor<CookieAuthenticationOptions> _cookieOptions;

    public SetupController(
        IAuthService authService,
        IDesktopSystemSettingsStore systemSettings,
        IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
    {
        _authService = authService;
        _systemSettings = systemSettings;
        _cookieOptions = cookieOptions;
    }

    [HttpGet("/setup/status")]
    public IActionResult SetupStatus()
    {
        var state = _authService.GetSetupState();
        if (!state.Succeeded || state.Payload is null)
        {
            return ApiResponse(state);
        }

        return ApiResponse(ApiOpResult<SetupStatusResponse>.Succeed(
            SetupStatusResponse.From(state.Payload, _systemSettings.Get())));
    }

    [HttpPost("/setup")]
    public async Task<IActionResult> Setup(SetupRequest request)
    {
        DesktopSystemSettingsDraft settingsDraft;
        try
        {
            settingsDraft = _systemSettings.Normalize(request.SystemSettings);
        }
        catch (ArgumentException exception)
        {
            return ApiResponse(ApiOpResult<SetupCompleteResponse>.Fail(400, exception.Message));
        }

        var result = _authService.Initialize(request.Username, request.Password);
        if (!result.Succeeded || result.Payload?.User is null)
        {
            return ApiResponse(result);
        }

        var savedSettings = _systemSettings.Save(settingsDraft);
        await _authService.SetUserAsync(
            new SessionUser(
                result.Payload.User.UserId,
                result.Payload.User.Username,
                result.Payload.User.Role,
                result.Payload.User.Enabled),
            _cookieOptions.Get(IAuthService.AuthenticationScheme).ExpireTimeSpan);

        return ApiResponse(
            ApiOpResult<SetupCompleteResponse>.Succeed(new SetupCompleteResponse(result.Payload, savedSettings)),
            StatusCodes.Status201Created);
    }
}

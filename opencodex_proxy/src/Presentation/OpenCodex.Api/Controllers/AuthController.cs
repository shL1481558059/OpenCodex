using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenCodex.Api.Configuration;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class AuthController : ApiControllerBase
{
    private readonly IAuthService _authService;
    private readonly ISessionService _sessionService;
    private readonly IDesktopSystemSettingsStore _systemSettings;
    private readonly IOptionsMonitor<CookieAuthenticationOptions> _cookieOptions;

    public AuthController(
        IAuthService authService,
        ISessionService sessionService,
        IDesktopSystemSettingsStore systemSettings,
        IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
    {
        _authService = authService;
        _sessionService = sessionService;
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
        await SessionState.SetUserAsync(
            HttpContext,
            new SessionUser(
                result.Payload.User.UserId,
                result.Payload.User.Username,
                result.Payload.User.Role,
                result.Payload.User.Enabled),
            _cookieOptions.Get(SessionState.AuthenticationScheme).ExpireTimeSpan);

        return ApiResponse(
            ApiOpResult<SetupCompleteResponse>.Succeed(new SetupCompleteResponse(result.Payload, savedSettings)),
            StatusCodes.Status201Created);
    }

    [HttpGet("/session")]
    public async Task<IActionResult> Session()
    {
        var user = SessionState.CurrentUser(HttpContext);
        if (user is null)
        {
            return ApiResponse(ApiOpResult<SessionResponse>.Succeed(SessionResponse.LoggedOut()));
        }

        try
        {
            user = _sessionService.RequireUser(user);
        }
        catch (BadRequestException exception) when (exception.StatusCode == StatusCodes.Status401Unauthorized)
        {
            await SessionState.ClearUserAsync(HttpContext);
            return ApiResponse(ApiOpResult<SessionResponse>.Succeed(SessionResponse.LoggedOut()));
        }

        return ApiResponse(ApiOpResult<SessionResponse>.Succeed(BuildSessionResponse(user)));
    }

    [HttpPost("/login")]
    public async Task<IActionResult> Login([FromForm] LoginRequest request)
    {
        var result = _authService.Login(request.Username, request.Password);
        if (result.Succeeded && result.Payload?.User is not null)
        {
            await SessionState.SetUserAsync(
                HttpContext,
                new SessionUser(
                    result.Payload.User.UserId,
                    result.Payload.User.Username,
                    result.Payload.User.Role,
                    result.Payload.User.Enabled),
                _cookieOptions.Get(SessionState.AuthenticationScheme).ExpireTimeSpan);
        }

        return ApiResponse(result);
    }

    [HttpPost("/logout")]
    public async Task<IActionResult> Logout()
    {
        await SessionState.ClearUserAsync(HttpContext);
        return ApiResponse(ApiOpResult<SessionResponse>.Succeed(SessionResponse.LoggedOut()));
    }

    private static SessionResponse BuildSessionResponse(SessionUser? user)
    {
        return user is null
            ? SessionResponse.LoggedOut()
            : SessionResponse.From(user.UserId, user.Username, user.Role, user.Enabled);
    }
}

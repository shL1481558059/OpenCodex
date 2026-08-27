using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class SessionController : ApiControllerBase
{
    private readonly IAuthService _authService;
    private readonly ISessionService _sessionService;
    private readonly IWorkContext _workContext;
    private readonly IOptionsMonitor<CookieAuthenticationOptions> _cookieOptions;

    public SessionController(
        IAuthService authService,
        ISessionService sessionService,
        IWorkContext workContext,
        IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
    {
        _authService = authService;
        _sessionService = sessionService;
        _workContext = workContext;
        _cookieOptions = cookieOptions;
    }

    [HttpGet("/session")]
    public async Task<IActionResult> Session()
    {
        var user = _workContext.CurrentUser;
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
            await _authService.ClearUserAsync();
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
            await _authService.SetUserAsync(
                new SessionUser(
                    result.Payload.User.UserId,
                    result.Payload.User.Username,
                    result.Payload.User.Role,
                    result.Payload.User.Enabled),
                _cookieOptions.Get(IAuthService.AuthenticationScheme).ExpireTimeSpan);
        }

        return ApiResponse(result);
    }

    [HttpPost("/logout")]
    public async Task<IActionResult> Logout()
    {
        await _authService.ClearUserAsync();
        return ApiResponse(ApiOpResult<SessionResponse>.Succeed(SessionResponse.LoggedOut()));
    }

    private static SessionResponse BuildSessionResponse(SessionUser? user)
    {
        return user is null
            ? SessionResponse.LoggedOut()
            : SessionResponse.From(user.UserId, user.Username, user.Role, user.Enabled);
    }
}

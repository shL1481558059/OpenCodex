using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class AuthController : ApiControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpGet("/session")]
    public IActionResult Session()
    {
        var user = SessionState.CurrentUser(HttpContext);
        return ApiResponse(ApiOpResult<SessionResponse>.Succeed(BuildSessionResponse(user)));
    }

    [HttpPost("/login")]
    public IActionResult Login([FromForm] LoginRequest request)
    {
        var result = _authService.Login(request.Username, request.Password);
        if (result.Succeeded && result.Payload?.User is not null)
        {
            SessionState.SetUser(
                HttpContext,
                new SessionUser(
                    result.Payload.User.Username,
                    result.Payload.User.Role,
                    result.Payload.User.Enabled));
        }

        return ApiResponse(result);
    }

    [HttpPost("/logout")]
    public IActionResult Logout()
    {
        SessionState.ClearUser(HttpContext);
        return ApiResponse(ApiOpResult<SessionResponse>.Succeed(SessionResponse.LoggedOut()));
    }

    private static SessionResponse BuildSessionResponse(SessionUser? user)
    {
        return user is null
            ? SessionResponse.LoggedOut()
            : SessionResponse.From(user.Username, user.Role, user.Enabled);
    }
}

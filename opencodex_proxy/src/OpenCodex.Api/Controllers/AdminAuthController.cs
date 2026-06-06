using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.DTOs.AdminAuth;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class AdminAuthController : ApiControllerBase
{
    private readonly IAdminAuthService _authService;
    private readonly IRequestBodyReader _bodyReader;

    public AdminAuthController(
        IAdminAuthService authService,
        IRequestBodyReader bodyReader)
    {
        _authService = authService;
        _bodyReader = bodyReader;
    }

    [HttpGet("/admin/api/session")]
    [ProducesResponseType(typeof(AdminSessionResponse), StatusCodes.Status200OK)]
    public IActionResult Session()
    {
        var user = AdminSession.CurrentUser(HttpContext);
        return Ok(SessionResponse(user));
    }

    [HttpPost("/admin/api/login")]
    [ProducesResponseType(typeof(AdminSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AdminLoginErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login()
    {
        var body = await _bodyReader.ReadFormOrJsonObjectAsync(
            Request,
            HttpContext.RequestAborted);
        var request = AdminLoginRequest.From(body);
        var result = _authService.Login(request.Username, request.Password);
        if (!result.Succeeded || result.Data is null)
        {
            return Unauthorized(new AdminLoginErrorResponse(result.Message));
        }

        var sessionUser = new AdminSessionUser(result.Data.Username, result.Data.Role, result.Data.Enabled);
        AdminSession.SetUser(HttpContext, sessionUser);
        return Ok(SessionResponse(sessionUser));
    }

    [HttpPost("/admin/logout")]
    [HttpPost("/admin/api/logout")]
    [ProducesResponseType(typeof(AdminSessionResponse), StatusCodes.Status200OK)]
    public IActionResult Logout()
    {
        AdminSession.ClearUser(HttpContext);
        return Ok(AdminSessionResponse.LoggedOut());
    }

    private static AdminSessionResponse SessionResponse(AdminSessionUser? user)
    {
        return user is null
            ? AdminSessionResponse.LoggedOut()
            : AdminSessionResponse.From(user.Username, user.Role, user.Enabled);
    }
}

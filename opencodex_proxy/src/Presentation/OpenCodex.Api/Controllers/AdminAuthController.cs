using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.AdminAuth;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Api.Controllers;

public sealed class AdminAuthController : ApiControllerBase
{
    private readonly IAdminAuthService _authService;

    public AdminAuthController(IAdminAuthService authService)
    {
        _authService = authService;
    }

    [HttpGet("/session")]
    public IActionResult Session()
    {
        var user = AdminSession.CurrentUser(HttpContext);
        return ApiResponse(ApiResult.Success(SessionResponse(user)));
    }

    [HttpPost("/login")]
    public IActionResult Login([FromForm] AdminLoginRequest request)
    {
        var result = _authService.Login(request.Username, request.Password);
        if (result.Succeeded && result.Data?.User is not null)
        {
            AdminSession.SetUser(
                HttpContext,
                new AdminSessionUser(
                    result.Data.User.Username,
                    result.Data.User.Role,
                    result.Data.User.Enabled));
        }

        return ApiResponse(result);
    }

    [HttpPost("/logout")]
    public IActionResult Logout()
    {
        AdminSession.ClearUser(HttpContext);
        return ApiResponse(ApiResult.Success(AdminSessionResponse.LoggedOut()));
    }

    private static AdminSessionResponse SessionResponse(AdminSessionUser? user)
    {
        return user is null
            ? AdminSessionResponse.LoggedOut()
            : AdminSessionResponse.From(user.Username, user.Role, user.Enabled);
    }
}

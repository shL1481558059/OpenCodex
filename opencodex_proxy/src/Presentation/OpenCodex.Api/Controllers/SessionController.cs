using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class SessionController : ApiControllerBase
{
    private readonly ISessionControllerService _session;

    public SessionController(
        ISessionControllerService session)
    {
        _session = session;
    }

    [HttpGet("/session")]
    public async Task<IActionResult> Session()
    {
        return ApiResponse(await _session.CurrentSessionAsync());
    }

    [HttpPost("/login")]
    public async Task<IActionResult> Login([FromForm] LoginRequest request)
    {
        return ApiResponse(await _session.LoginAsync(request.Username, request.Password));
    }

    [HttpPost("/logout")]
    public async Task<IActionResult> Logout()
    {
        return ApiResponse(await _session.LogoutAsync());
    }
}

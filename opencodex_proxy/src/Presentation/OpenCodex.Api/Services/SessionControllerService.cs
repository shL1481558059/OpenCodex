using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Services;

/// <summary>
/// 会话管理实现：读取当前用户、登录写 cookie、登出清 cookie。
/// </summary>
public sealed class SessionControllerService : ISessionControllerService
{
    private readonly IAuthService _authService;
    private readonly ISessionService _sessionService;
    private readonly IWorkContext _workContext;
    private readonly IOptionsMonitor<CookieAuthenticationOptions> _cookieOptions;

    public SessionControllerService(
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

    public async Task<ApiOpResult<SessionResponse>> CurrentSessionAsync()
    {
        var user = _workContext.CurrentUser;
        if (user is null)
        {
            return SucceedLoggedOut();
        }

        try
        {
            user = _sessionService.RequireUser(user);
        }
        catch (BadRequestException exception) when (exception.StatusCode == StatusCodes.Status401Unauthorized)
        {
            await _authService.ClearUserAsync();
            return SucceedLoggedOut();
        }

        return ApiOpResult<SessionResponse>.Succeed(BuildSessionResponse(user));
    }

    public async Task<ApiOpResult<SessionResponse>> LoginAsync(
        string? username,
        string? password)
    {
        var result = _authService.Login(username, password);
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

        return result;
    }

    public async Task<ApiOpResult<SessionResponse>> LogoutAsync()
    {
        await _authService.ClearUserAsync();
        return SucceedLoggedOut();
    }

    private static ApiOpResult<SessionResponse> SucceedLoggedOut()
        => ApiOpResult<SessionResponse>.Succeed(SessionResponse.LoggedOut());

    private static SessionResponse BuildSessionResponse(SessionUser user)
        => SessionResponse.From(user.UserId, user.Username, user.Role, user.Enabled);
}

using OpenCodex.Api.Controllers;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Infrastructure;

public sealed class WebWorkContext : IWorkContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ISessionService _session;

    public WebWorkContext(
        IHttpContextAccessor httpContextAccessor,
        ISessionService session)
    {
        _httpContextAccessor = httpContextAccessor;
        _session = session;
    }

    public SessionUser? CurrentUser
    {
        get
        {
            var context = _httpContextAccessor.HttpContext;
            return context is null ? null : SessionState.CurrentUser(context);
        }
    }

    public bool IsSignedIn => CurrentUser is not null;

    public bool IsSuperadmin => CurrentUser is not null && SessionState.IsSuperadmin(CurrentUser);

    public SessionUser RequireUser()
    {
        return RefreshSessionUser(_session.RequireUser);
    }

    public SessionUser RequireSuperadmin()
    {
        return RefreshSessionUser(_session.RequireSuperadmin);
    }

    private SessionUser RefreshSessionUser(
        Func<SessionUser?, SessionUser> require)
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new BadRequestException(
                "admin authentication required",
                StatusCodes.Status401Unauthorized);

        try
        {
            var user = require(SessionState.CurrentUser(context));
            SessionState.SetUser(context, user);
            return user;
        }
        catch (BadRequestException exception) when (exception.StatusCode == StatusCodes.Status401Unauthorized)
        {
            SessionState.ClearUser(context);
            throw;
        }
    }
}

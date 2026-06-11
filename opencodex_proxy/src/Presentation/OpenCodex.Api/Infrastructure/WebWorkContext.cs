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
        return Require(_session.RequireUser);
    }

    public SessionUser RequireSuperadmin()
    {
        return Require(_session.RequireSuperadmin);
    }

    private SessionUser Require(
        Func<SessionUser?, SessionUser> require)
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new BadRequestException(
                "admin authentication required",
                StatusCodes.Status401Unauthorized);

        try
        {
            return require(SessionState.CurrentUser(context));
        }
        catch (BadRequestException exception) when (exception.StatusCode == StatusCodes.Status401Unauthorized)
        {
            SessionState.ClearUserAsync(context).GetAwaiter().GetResult();
            throw;
        }
    }
}

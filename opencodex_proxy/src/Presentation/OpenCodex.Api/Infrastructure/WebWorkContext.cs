using System.Security.Claims;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Infrastructure;

public sealed class WebWorkContext : IWorkContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ISessionService _session;
    private readonly IAuthService _authService;

    private const string UserIdClaimType = "opencodex_admin_user_id";
    private const string EnabledClaimType = "opencodex_admin_enabled";

    public WebWorkContext(
        IHttpContextAccessor httpContextAccessor,
        ISessionService session,
        IAuthService authService)
    {
        _httpContextAccessor = httpContextAccessor;
        _session = session;
        _authService = authService;
    }

    public SessionUser? CurrentUser
    {
        get
        {
            var context = _httpContextAccessor.HttpContext;
            if (context is null) return null;

            var principal = context.User;
            if (principal?.Identity?.IsAuthenticated != true) return null;

            var userIdString = principal.FindFirstValue(UserIdClaimType);
            var username = principal.FindFirstValue(ClaimTypes.Name);
            var role = principal.FindFirstValue(ClaimTypes.Role);
            if (string.IsNullOrWhiteSpace(userIdString)
                || string.IsNullOrWhiteSpace(username)
                || string.IsNullOrWhiteSpace(role)
                || !Guid.TryParse(userIdString, out var userId))
            {
                return null;
            }

            return new SessionUser(
                userId,
                username.Trim(),
                role.Trim(),
                !string.Equals(principal.FindFirstValue(EnabledClaimType), "false", StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool IsSignedIn => CurrentUser is not null;

    public bool IsSuperadmin => CurrentUser is not null && CurrentUser.Role == "superadmin";

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
        if (_httpContextAccessor.HttpContext is null)
        {
            throw new BadRequestException(
                "admin authentication required",
                StatusCodes.Status401Unauthorized);
        }

        try
        {
            return require(CurrentUser);
        }
        catch (BadRequestException exception) when (exception.StatusCode == StatusCodes.Status401Unauthorized)
        {
            _authService.ClearUserAsync().GetAwaiter().GetResult();
            throw;
        }
    }
}

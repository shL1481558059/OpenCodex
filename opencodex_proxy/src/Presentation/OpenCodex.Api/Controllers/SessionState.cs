using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using OpenCodex.CoreBase.Domain;

namespace OpenCodex.Api.Controllers;

internal static class SessionState
{
    public const string AuthenticationScheme = "OpenCodexAdmin";
    public const string CookieName = "opencodex_admin_auth";

    private const string EnabledClaimType = "opencodex_admin_enabled";

    public static SessionUser? CurrentUser(HttpContext context)
    {
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var username = principal.FindFirstValue(ClaimTypes.Name);
        var role = principal.FindFirstValue(ClaimTypes.Role);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        return new SessionUser(
            username.Trim(),
            role.Trim(),
            !string.Equals(principal.FindFirstValue(EnabledClaimType), "false", StringComparison.OrdinalIgnoreCase));
    }

    public static Task SetUserAsync(
        HttpContext context,
        SessionUser user,
        TimeSpan persistentLifetime)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(EnabledClaimType, user.Enabled ? "true" : "false")
        };
        var identity = new ClaimsIdentity(
            claims,
            AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var now = DateTimeOffset.UtcNow;
        context.User = principal;
        return context.SignInAsync(
            AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                IssuedUtc = now,
                ExpiresUtc = now.Add(persistentLifetime)
            });
    }

    public static Task ClearUserAsync(HttpContext context)
    {
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        return context.SignOutAsync(AuthenticationScheme);
    }

    public static bool IsSuperadmin(SessionUser user)
    {
        return user.Role == "superadmin";
    }
}

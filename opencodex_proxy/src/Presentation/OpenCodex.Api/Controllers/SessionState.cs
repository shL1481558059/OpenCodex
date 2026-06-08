using OpenCodex.CoreBase.Domain;

namespace OpenCodex.Api.Controllers;

internal static class SessionState
{
    private const string UsernameKey = "opencodex_admin_username";
    private const string RoleKey = "opencodex_admin_role";
    private const string EnabledKey = "opencodex_admin_enabled";

    public static SessionUser? CurrentUser(HttpContext context)
    {
        var username = context.Session.GetString(UsernameKey);
        var role = context.Session.GetString(RoleKey);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        return new SessionUser(
            username.Trim(),
            role.Trim(),
            context.Session.GetString(EnabledKey) != "false");
    }

    public static void SetUser(HttpContext context, SessionUser user)
    {
        context.Session.SetString(UsernameKey, user.Username);
        context.Session.SetString(RoleKey, user.Role);
        context.Session.SetString(EnabledKey, user.Enabled ? "true" : "false");
    }

    public static void ClearUser(HttpContext context)
    {
        context.Session.Clear();
    }

    public static bool IsSuperadmin(SessionUser user)
    {
        return user.Role == "superadmin";
    }
}

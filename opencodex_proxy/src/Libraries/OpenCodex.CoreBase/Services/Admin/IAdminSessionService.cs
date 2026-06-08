namespace OpenCodex.CoreBase.Services.Admin;

public interface IAdminSessionService
{
    AdminSessionUser RequireUser(AdminSessionUser? currentUser);

    AdminSessionUser RequireSuperadmin(AdminSessionUser? currentUser);
}

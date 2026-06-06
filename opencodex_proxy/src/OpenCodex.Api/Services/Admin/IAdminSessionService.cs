namespace OpenCodex.Api.Services;

public interface IAdminSessionService
{
    AdminSessionUser RequireUser(AdminSessionUser? currentUser);

    AdminSessionUser RequireSuperadmin(AdminSessionUser? currentUser);
}

using OpenCodex.Api.Errors;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;

namespace OpenCodex.Api.Services;

public sealed class AdminSessionService : IAdminSessionService
{
    private const string AuthenticationRequiredMessage = "admin authentication required";

    private readonly IRepository<UserEntity> _users;

    public AdminSessionService(IRepository<UserEntity> users)
    {
        _users = users;
    }

    public AdminSessionUser RequireUser(AdminSessionUser? currentUser)
    {
        if (currentUser is null)
        {
            throw Unauthorized();
        }

        var storedUser = _users.GetById(currentUser.Username);
        if (storedUser is null || !storedUser.Enabled)
        {
            throw Unauthorized();
        }

        return new AdminSessionUser(storedUser.Username, storedUser.Role, storedUser.Enabled);
    }

    public AdminSessionUser RequireSuperadmin(AdminSessionUser? currentUser)
    {
        var user = RequireUser(currentUser);
        if (user.Role != "superadmin")
        {
            throw new BadRequestException("superadmin required", ProxyHttpStatus.Forbidden);
        }

        return user;
    }

    private static BadRequestException Unauthorized()
    {
        return new BadRequestException(
            AuthenticationRequiredMessage,
            ProxyHttpStatus.Unauthorized);
    }
}

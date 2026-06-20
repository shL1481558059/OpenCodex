using OpenCodex.Core.Errors;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class SessionService : ISessionService
{
    private const string AuthenticationRequiredMessage = "admin authentication required";

    private readonly IRepository<User> _userRepository;

    public SessionService(IRepository<User> userRepository)
    {
        _userRepository = userRepository;
    }

    public SessionUser RequireUser(SessionUser? currentUser)
    {
        if (currentUser is null)
        {
            throw Unauthorized();
        }

        var storedUser = _userRepository.TableNoTracking
            .FirstOrDefault(user => user.Id == currentUser.UserId);
        if (storedUser is null || !storedUser.Enabled)
        {
            throw Unauthorized();
        }

        return new SessionUser(storedUser.Id, storedUser.Username, storedUser.Role, storedUser.Enabled);
    }

    public SessionUser RequireSuperadmin(SessionUser? currentUser)
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

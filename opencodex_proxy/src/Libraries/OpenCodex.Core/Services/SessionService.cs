using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class SessionService : ISessionService
{
    private const string AuthenticationRequiredMessage = "admin authentication required";

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public SessionService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public SessionUser RequireUser(SessionUser? currentUser)
    {
        if (currentUser is null)
        {
            throw Unauthorized();
        }

        var settings = _settingsProvider.GetSettings();
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var storedUser = context.Users
            .AsNoTracking()
            .FirstOrDefault(user => user.Username == currentUser.Username);
        if (storedUser is null || !storedUser.Enabled)
        {
            throw Unauthorized();
        }

        return new SessionUser(storedUser.Username, storedUser.Role, storedUser.Enabled);
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

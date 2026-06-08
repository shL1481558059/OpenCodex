using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.Ef;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Core.Services.Admin;

public sealed class AdminSessionService : IAdminSessionService
{
    private const string AuthenticationRequiredMessage = "admin authentication required";

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public AdminSessionService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public AdminSessionUser RequireUser(AdminSessionUser? currentUser)
    {
        if (currentUser is null)
        {
            throw Unauthorized();
        }

        var settings = _settingsProvider.GetSettings();
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var storedUser = context.Users
            .AsNoTracking()
            .FirstOrDefault(user => user.Username == currentUser.Username);
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

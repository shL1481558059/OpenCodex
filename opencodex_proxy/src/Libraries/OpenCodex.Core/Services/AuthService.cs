using Mapster;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class AuthService : IAuthService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public AuthService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public ApiOpResult<SessionResponse> Login(string? username, string? password)
    {
        var settings = _settingsProvider.GetSettings();
        EnsureConfiguredSuperadmin(settings);

        var normalizedUsername = (username ?? string.Empty).Trim();
        if (normalizedUsername.Length == 0)
        {
            normalizedUsername = settings.AdminUsername;
        }

        var user = AuthenticateUser(settings.DbPath, normalizedUsername, (password ?? string.Empty).Trim());
        if (user is null)
        {
            return ApiOpResult<SessionResponse>.Fail(
                401,
                "用户名或密码错误");
        }

        return ApiOpResult<SessionResponse>.Succeed(
            SessionResponse.From(user.Username, user.Role, user.Enabled));
    }

    private void EnsureConfiguredSuperadmin(OpenCodexRuntimeSettings settings)
    {
        if (settings.AdminPassword.Length == 0)
        {
            return;
        }

        EnsureSuperadmin(settings.DbPath, settings.AdminUsername, settings.AdminPassword);
    }

    private static void EnsureSuperadmin(string dbPath, string username, string password)
    {
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            username = "admin";
        }

        using var context = OpenCodexDbContextFactory.Create(dbPath);
        var now = UnixTimeSeconds();
        var user = context.Users.FirstOrDefault(item => item.Username == username);
        if (user is null)
        {
            user = new User
            {
                Username = username,
                PasswordHash = OpenCodexSecurity.HashPassword(password),
                Role = "superadmin",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            context.Users.Add(user);
        }
        else
        {
            user.PasswordHash = OpenCodexSecurity.HashPassword(password);
            user.Role = "superadmin";
            user.Enabled = true;
            user.UpdatedAt = now;
        }

        context.SaveChanges();
    }

    private static UserDto? AuthenticateUser(
        string dbPath,
        string username,
        string password)
    {
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            return null;
        }

        using var context = OpenCodexDbContextFactory.Create(dbPath);
        var user = context.Users
            .AsNoTracking()
            .FirstOrDefault(item => item.Username == username);
        if (user is null || !user.Enabled)
        {
            return null;
        }

        return OpenCodexSecurity.VerifyPassword(password, user.PasswordHash)
            ? user.Adapt<UserDto>()
            : null;
    }

    private static string NormalizeUsername(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}

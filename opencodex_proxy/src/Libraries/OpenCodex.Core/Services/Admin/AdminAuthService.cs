using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.Core.Services.Ef;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.AdminAuth;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Core.Services.Admin;

public sealed class AdminAuthService : IAdminAuthService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public AdminAuthService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public ApiResult<AdminSessionResponse> Login(string? username, string? password)
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
            return ApiResult.Fail<AdminSessionResponse>(
                AdminAuthErrorCodes.InvalidCredentials,
                "用户名或密码错误");
        }

        return ApiResult.Success(
            AdminSessionResponse.From(user.Username, user.Role, user.Enabled));
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
        username = EfServiceSupport.NormalizeUsername(username);
        if (username.Length == 0)
        {
            username = "admin";
        }

        EfServiceSupport.InitializeDatabase(dbPath, username);
        using var context = OpenCodexDbContextFactory.Create(dbPath);
        var now = EfServiceSupport.UnixTimeSeconds();
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
        EfServiceSupport.InitializeDatabase(dbPath, username);
        username = EfServiceSupport.NormalizeUsername(username);
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
            ? EfServiceSupport.ToUserDto(user)
            : null;
    }
}

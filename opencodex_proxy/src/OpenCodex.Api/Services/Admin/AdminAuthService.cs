using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public sealed class AdminAuthService : IAdminAuthService
{
    public const int InvalidCredentialsCode = 401001;

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IAdminUserRepository _users;

    public AdminAuthService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IAdminUserRepository users)
    {
        _settingsProvider = settingsProvider;
        _users = users;
    }

    public ServiceResult<AdminAuthenticatedUser> Login(string? username, string? password)
    {
        var settings = _settingsProvider.GetSettings();
        EnsureConfiguredSuperadmin(settings);

        var normalizedUsername = (username ?? string.Empty).Trim();
        if (normalizedUsername.Length == 0)
        {
            normalizedUsername = settings.AdminUsername;
        }

        var user = _users.AuthenticateUser(normalizedUsername, (password ?? string.Empty).Trim());
        if (user is null)
        {
            return ServiceResult.Fail<AdminAuthenticatedUser>(
                InvalidCredentialsCode,
                "用户名或密码错误");
        }

        return ServiceResult.Success(
            new AdminAuthenticatedUser(user.Username, user.Role, user.Enabled));
    }

    private void EnsureConfiguredSuperadmin(OpenCodexRuntimeSettings settings)
    {
        if (settings.AdminPassword.Length == 0)
        {
            return;
        }

        _users.EnsureSuperadmin(settings.AdminUsername, settings.AdminPassword);
    }
}

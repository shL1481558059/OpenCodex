using Mapster;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class AuthService : IAuthService
{
    private readonly IRepository<User> _userRepository;
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public AuthService(
        IRepository<User> userRepository,
        IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _userRepository = userRepository;
        _settingsProvider = settingsProvider;
    }

    public ApiOpResult<SessionResponse> Login(string? username, string? password)
    {
        EnsureConfiguredSuperadmin();

        var normalizedUsername = (username ?? string.Empty).Trim();
        if (normalizedUsername.Length == 0)
        {
            normalizedUsername = _settingsProvider.GetSettings().AdminUsername;
        }

        var user = AuthenticateUser(normalizedUsername, (password ?? string.Empty).Trim());
        if (user is null)
        {
            return ApiOpResult<SessionResponse>.Fail(
                401,
                "用户名或密码错误");
        }

        return ApiOpResult<SessionResponse>.Succeed(
            SessionResponse.From(user.Id, user.Username, user.Role, user.Enabled));
    }

    private void EnsureConfiguredSuperadmin()
    {
        var settings = _settingsProvider.GetSettings();
        if (settings.AdminPassword.Length == 0)
        {
            return;
        }

        var username = NormalizeUsername(settings.AdminUsername);
        if (username.Length == 0)
        {
            username = "admin";
        }

        var existing = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == username);
        var now = UnixTimeSeconds();
        if (existing is null)
        {
            _userRepository.Insert(new User
            {
                Username = username,
                PasswordHash = OpenCodexSecurity.HashPassword(settings.AdminPassword),
                Role = "superadmin",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.PasswordHash = OpenCodexSecurity.HashPassword(settings.AdminPassword);
            existing.Role = "superadmin";
            existing.Enabled = true;
            existing.UpdatedAt = now;
            _userRepository.Update(existing);
        }
    }

    private UserDto? AuthenticateUser(string username, string password)
    {
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            return null;
        }

        var user = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == username);
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

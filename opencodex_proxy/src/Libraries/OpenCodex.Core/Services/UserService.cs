using Mapster;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Users;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class UserService : IUserService
{
    private static readonly HashSet<string> UserRoles = new(StringComparer.Ordinal)
    {
        "superadmin",
        "user"
    };

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IWorkContext _workContext;

    public UserService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IWorkContext workContext)
    {
        _settingsProvider = settingsProvider;
        _workContext = workContext;
    }

    public ApiOpResult<UsersResponse> ListUsers()
    {
        var settings = _settingsProvider.GetSettings();
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        return ApiOpResult<UsersResponse>.Succeed(UsersResponse.From(context.Users
            .AsNoTracking()
            .OrderBy(user => user.Role)
            .ThenBy(user => user.Username)
            .AsEnumerable()
            .Select(user => user.Adapt<UserDto>())
            .ToList()));
    }

    public ApiOpResult<UserResponsePayload> CreateUser(
        UserCreateCommand command)
    {
        try
        {
            var user = CreateUser(
                _settingsProvider.GetSettings(),
                command.Username.Trim(),
                command.Password.Trim(),
                enabled: command.Enabled);
            return ApiOpResult<UserResponsePayload>.Succeed(UserResponsePayload.From(user));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    public ApiOpResult<UserResponsePayload> UpdateUser(
        string username,
        UserUpdateCommand command)
    {
        try
        {
            var settings = _settingsProvider.GetSettings();
            UserDto user;
            if (command.Enabled.HasValue)
            {
                user = SetUserEnabled(
                    settings,
                    username,
                    command.Enabled.Value);
            }
            else
            {
                user = GetUser(settings, username)
                    ?? throw new InvalidOperationException("user not found");
            }

            if (command.Password is not null)
            {
                if (string.Equals(
                    username,
                    settings.AdminUsername,
                    StringComparison.Ordinal))
                {
                    return ValidationFailure("environment superadmin password is managed by env");
                }

                user = ResetUserPassword(settings, username, command.Password.Trim());
            }

            return ApiOpResult<UserResponsePayload>.Succeed(UserResponsePayload.From(user));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (InvalidOperationException exception) when (exception.Message == "user not found")
        {
            return ApiOpResult<UserResponsePayload>.Fail(404, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    public ApiOpResult<DeleteUserResponse> DeleteUser(
        string username)
    {
        try
        {
            var currentUser = _workContext.RequireUser();
            var user = DeleteUser(
                _settingsProvider.GetSettings(),
                username,
                currentUser.Username);
            return ApiOpResult<DeleteUserResponse>.Succeed(DeleteUserResponse.From(user));
        }
        catch (ArgumentException exception)
        {
            return ApiOpResult<DeleteUserResponse>.Fail(400, exception.Message);
        }
        catch (InvalidOperationException exception) when (exception.Message == "user not found")
        {
            return ApiOpResult<DeleteUserResponse>.Fail(404, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ApiOpResult<DeleteUserResponse>.Fail(400, exception.Message);
        }
    }

    private static ApiOpResult<UserResponsePayload> ValidationFailure(string message)
    {
        return ApiOpResult<UserResponsePayload>.Fail(400, message);
    }

    private static UserDto CreateUser(
        OpenCodexRuntimeSettings settings,
        string username,
        string password,
        string role = "user",
        bool enabled = true)
    {
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (!UserRoles.Contains(role))
        {
            throw new ArgumentException("role is invalid", nameof(role));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("password is required", nameof(password));
        }

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        if (context.Users.Any(user => user.Username == username))
        {
            throw new ArgumentException("username already exists", nameof(username));
        }

        var now = UnixTimeSeconds();
        var created = new User
        {
            Username = username,
            PasswordHash = OpenCodexSecurity.HashPassword(password),
            Role = role,
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.Users.Add(created);

        try
        {
            context.SaveChanges();
        }
        catch (DbUpdateException exception)
        {
            throw new ArgumentException("username already exists", nameof(username), exception);
        }

        return created.Adapt<UserDto>();
    }

    private static UserDto? GetUser(OpenCodexRuntimeSettings settings, string username)
    {
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            return null;
        }

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var user = context.Users
            .AsNoTracking()
            .FirstOrDefault(item => item.Username == username);
        return user is null ? null : user.Adapt<UserDto>();
    }

    private static UserDto SetUserEnabled(
        OpenCodexRuntimeSettings settings,
        string username,
        bool enabled)
    {
        username = NormalizeUsername(username);
        var protectedUsername = NormalizeUsername(settings.AdminUsername);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (protectedUsername.Length > 0 && username == protectedUsername && !enabled)
        {
            throw new InvalidOperationException("cannot disable the environment superadmin");
        }

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var user = context.Users.FirstOrDefault(item => item.Username == username)
            ?? throw new InvalidOperationException("user not found");
        user.Enabled = enabled;
        user.UpdatedAt = UnixTimeSeconds();
        context.SaveChanges();
        return user.Adapt<UserDto>();
    }

    private static UserDto ResetUserPassword(
        OpenCodexRuntimeSettings settings,
        string username,
        string password)
    {
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("password is required", nameof(password));
        }

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var user = context.Users.FirstOrDefault(item => item.Username == username)
            ?? throw new InvalidOperationException("user not found");
        user.PasswordHash = OpenCodexSecurity.HashPassword(password);
        user.UpdatedAt = UnixTimeSeconds();
        context.SaveChanges();
        return user.Adapt<UserDto>();
    }

    private static UserDto DeleteUser(
        OpenCodexRuntimeSettings settings,
        string username,
        string protectedUsername)
    {
        username = NormalizeUsername(username);
        protectedUsername = NormalizeUsername(protectedUsername);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (protectedUsername.Length == 0)
        {
            throw new ArgumentException("protected_username is required", nameof(protectedUsername));
        }

        if (username == protectedUsername)
        {
            throw new InvalidOperationException("cannot delete current user");
        }

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        using var transaction = context.Database.BeginTransaction();
        var user = context.Users.FirstOrDefault(item => item.Username == username)
            ?? throw new InvalidOperationException("user not found");
        var deleted = user.Adapt<UserDto>();
        context.AccessApiKeys.RemoveRange(context.AccessApiKeys.Where(key => key.OwnerUsername == username));
        context.Channels.RemoveRange(context.Channels.Where(channel => channel.OwnerUsername == username));
        context.Users.Remove(user);
        context.SaveChanges();
        transaction.Commit();
        return deleted;
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

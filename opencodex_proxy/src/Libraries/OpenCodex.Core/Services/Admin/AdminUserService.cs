using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.Core.Services.Ef;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.AdminUsers;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Core.Services.Admin;

public sealed class AdminUserService : IAdminUserService
{
    private static readonly HashSet<string> UserRoles = new(StringComparer.Ordinal)
    {
        "superadmin",
        "user"
    };

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public AdminUserService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public ApiResult<UsersResponse> ListUsers()
    {
        var settings = _settingsProvider.GetSettings();
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        return ApiResult.Success(UsersResponse.From(context.Users
            .AsNoTracking()
            .OrderBy(user => user.Role)
            .ThenBy(user => user.Username)
            .AsEnumerable()
            .Select(EfServiceSupport.ToUserDto)
            .ToList()));
    }

    public ApiResult<UserResponsePayload> CreateUser(
        AdminUserCreateCommand command)
    {
        try
        {
            var user = CreateUser(
                _settingsProvider.GetSettings(),
                command.Username.Trim(),
                command.Password.Trim(),
                enabled: command.Enabled);
            return ApiResult.Success(UserResponsePayload.From(user));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    public ApiResult<UserResponsePayload> UpdateUser(
        string username,
        AdminUserUpdateCommand command)
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

            return ApiResult.Success(UserResponsePayload.From(user));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (InvalidOperationException exception) when (exception.Message == "user not found")
        {
            return ApiResult.Fail<UserResponsePayload>(AdminUserErrorCodes.NotFound, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    public ApiResult<DeleteUserResponse> DeleteUser(
        string username,
        string currentUsername)
    {
        try
        {
            var user = DeleteUser(
                _settingsProvider.GetSettings(),
                username,
                currentUsername);
            return ApiResult.Success(DeleteUserResponse.From(user));
        }
        catch (ArgumentException exception)
        {
            return ApiResult.Fail<DeleteUserResponse>(AdminUserErrorCodes.Validation, exception.Message);
        }
        catch (InvalidOperationException exception) when (exception.Message == "user not found")
        {
            return ApiResult.Fail<DeleteUserResponse>(AdminUserErrorCodes.NotFound, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ApiResult.Fail<DeleteUserResponse>(AdminUserErrorCodes.Validation, exception.Message);
        }
    }

    private static ApiResult<UserResponsePayload> ValidationFailure(string message)
    {
        return ApiResult.Fail<UserResponsePayload>(AdminUserErrorCodes.Validation, message);
    }

    private static UserDto CreateUser(
        OpenCodexRuntimeSettings settings,
        string username,
        string password,
        string role = "user",
        bool enabled = true)
    {
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        username = EfServiceSupport.NormalizeUsername(username);
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

        var now = EfServiceSupport.UnixTimeSeconds();
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

        return EfServiceSupport.ToUserDto(created);
    }

    private static UserDto? GetUser(OpenCodexRuntimeSettings settings, string username)
    {
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        username = EfServiceSupport.NormalizeUsername(username);
        if (username.Length == 0)
        {
            return null;
        }

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var user = context.Users
            .AsNoTracking()
            .FirstOrDefault(item => item.Username == username);
        return user is null ? null : EfServiceSupport.ToUserDto(user);
    }

    private static UserDto SetUserEnabled(
        OpenCodexRuntimeSettings settings,
        string username,
        bool enabled)
    {
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        username = EfServiceSupport.NormalizeUsername(username);
        var protectedUsername = EfServiceSupport.NormalizeUsername(settings.AdminUsername);
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
        user.UpdatedAt = EfServiceSupport.UnixTimeSeconds();
        context.SaveChanges();
        return EfServiceSupport.ToUserDto(user);
    }

    private static UserDto ResetUserPassword(
        OpenCodexRuntimeSettings settings,
        string username,
        string password)
    {
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        username = EfServiceSupport.NormalizeUsername(username);
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
        user.UpdatedAt = EfServiceSupport.UnixTimeSeconds();
        context.SaveChanges();
        return EfServiceSupport.ToUserDto(user);
    }

    private static UserDto DeleteUser(
        OpenCodexRuntimeSettings settings,
        string username,
        string protectedUsername)
    {
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        username = EfServiceSupport.NormalizeUsername(username);
        protectedUsername = EfServiceSupport.NormalizeUsername(protectedUsername);
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
        var deleted = EfServiceSupport.ToUserDto(user);
        context.AccessApiKeys.RemoveRange(context.AccessApiKeys.Where(key => key.OwnerUsername == username));
        context.Channels.RemoveRange(context.Channels.Where(channel => channel.OwnerUsername == username));
        context.Users.Remove(user);
        context.SaveChanges();
        transaction.Commit();
        return deleted;
    }
}

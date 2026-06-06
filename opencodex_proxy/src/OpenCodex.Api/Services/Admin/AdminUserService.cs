using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public sealed class AdminUserService : IAdminUserService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IAdminUserRepository _users;

    public AdminUserService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IAdminUserRepository users)
    {
        _settingsProvider = settingsProvider;
        _users = users;
    }

    public ServiceResult<IReadOnlyList<UserRecord>> ListUsers()
    {
        return ServiceResult.Success(_users.ListUsers());
    }

    public ServiceResult<UserRecord> CreateUser(
        AdminUserCreateCommand command)
    {
        try
        {
            var user = _users.CreateUser(
                command.Username.Trim(),
                command.Password.Trim(),
                enabled: command.Enabled);
            return ServiceResult.Success(user);
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    public ServiceResult<UserRecord> UpdateUser(
        string username,
        AdminUserUpdateCommand command)
    {
        try
        {
            UserRecord user;
            if (command.Enabled.HasValue)
            {
                user = _users.SetUserEnabled(
                    username,
                    command.Enabled.Value,
                    _settingsProvider.GetSettings().AdminUsername);
            }
            else
            {
                user = _users.GetUser(username)
                    ?? throw new InvalidOperationException("user not found");
            }

            if (command.Password is not null)
            {
                if (string.Equals(
                    username,
                    _settingsProvider.GetSettings().AdminUsername,
                    StringComparison.Ordinal))
                {
                    return ValidationFailure("environment superadmin password is managed by env");
                }

                user = _users.ResetUserPassword(username, command.Password.Trim());
            }

            return ServiceResult.Success(user);
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (InvalidOperationException exception) when (exception.Message == "user not found")
        {
            return ServiceResult.Fail<UserRecord>(AdminUserErrorCodes.NotFound, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    public ServiceResult<UserRecord> DeleteUser(
        string username,
        string currentUsername)
    {
        try
        {
            return ServiceResult.Success(_users.DeleteUser(username, currentUsername));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (InvalidOperationException exception) when (exception.Message == "user not found")
        {
            return ServiceResult.Fail<UserRecord>(AdminUserErrorCodes.NotFound, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    private static ServiceResult<UserRecord> ValidationFailure(string message)
    {
        return ServiceResult.Fail<UserRecord>(AdminUserErrorCodes.Validation, message);
    }
}

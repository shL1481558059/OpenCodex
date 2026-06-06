using OpenCodex.Api.Domain;
using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public interface IAdminUserService
{
    ServiceResult<IReadOnlyList<UserRecord>> ListUsers();

    ServiceResult<UserRecord> CreateUser(
        AdminUserCreateCommand command);

    ServiceResult<UserRecord> UpdateUser(
        string username,
        AdminUserUpdateCommand command);

    ServiceResult<UserRecord> DeleteUser(
        string username,
        string currentUsername);
}

public sealed class AdminUserCreateCommand
{
    public AdminUserCreateCommand(string username, string password, bool enabled)
    {
        Username = username;
        Password = password;
        Enabled = enabled;
    }

    public string Username { get; }

    public string Password { get; }

    public bool Enabled { get; }
}

public sealed class AdminUserUpdateCommand
{
    public AdminUserUpdateCommand(bool? enabled, string? password)
    {
        Enabled = enabled;
        Password = password;
    }

    public bool? Enabled { get; }

    public string? Password { get; }
}

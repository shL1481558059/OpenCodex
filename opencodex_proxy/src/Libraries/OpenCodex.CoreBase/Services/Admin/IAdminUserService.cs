using OpenCodex.CoreBase.DTOs.AdminUsers;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services.Admin;

public interface IAdminUserService
{
    ApiResult<UsersResponse> ListUsers();

    ApiResult<UserResponsePayload> CreateUser(
        AdminUserCreateCommand command);

    ApiResult<UserResponsePayload> UpdateUser(
        string username,
        AdminUserUpdateCommand command);

    ApiResult<DeleteUserResponse> DeleteUser(
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

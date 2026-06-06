using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public interface IAdminUserRepository
{
    UserRecord EnsureSuperadmin(string username, string password);

    UserRecord? AuthenticateUser(string username, string password);

    IReadOnlyList<UserRecord> ListUsers();

    UserRecord CreateUser(string username, string password, bool enabled);

    UserRecord? GetUser(string username);

    UserRecord SetUserEnabled(string username, bool enabled, string protectedUsername);

    UserRecord ResetUserPassword(string username, string password);

    UserRecord DeleteUser(string username, string protectedUsername);
}

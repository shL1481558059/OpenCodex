using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public sealed class AdminUserRepository : IAdminUserRepository
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IRepository<UserEntity> _users;

    public AdminUserRepository(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IRepository<UserEntity> users)
    {
        _settingsProvider = settingsProvider;
        _users = users;
    }

    public UserRecord EnsureSuperadmin(string username, string password)
    {
        return OpenCodexDatabase.EnsureSuperadmin(_settingsProvider.GetSettings().DbPath, username, password);
    }

    public UserRecord? AuthenticateUser(string username, string password)
    {
        return OpenCodexDatabase.AuthenticateUser(_settingsProvider.GetSettings().DbPath, username, password);
    }

    public IReadOnlyList<UserRecord> ListUsers()
    {
        return _users.ListAll()
            .Select(user => user.ToRecord())
            .ToList();
    }

    public UserRecord CreateUser(string username, string password, bool enabled)
    {
        return OpenCodexDatabase.CreateUser(
            _settingsProvider.GetSettings().DbPath,
            username,
            password,
            enabled: enabled);
    }

    public UserRecord? GetUser(string username)
    {
        return _users.GetById(username)?.ToRecord();
    }

    public UserRecord SetUserEnabled(string username, bool enabled, string protectedUsername)
    {
        return OpenCodexDatabase.SetUserEnabled(
            _settingsProvider.GetSettings().DbPath,
            username,
            enabled,
            protectedUsername);
    }

    public UserRecord ResetUserPassword(string username, string password)
    {
        return OpenCodexDatabase.ResetUserPassword(
            _settingsProvider.GetSettings().DbPath,
            username,
            password);
    }

    public UserRecord DeleteUser(string username, string protectedUsername)
    {
        return OpenCodexDatabase.DeleteUser(
            _settingsProvider.GetSettings().DbPath,
            username,
            protectedUsername);
    }
}

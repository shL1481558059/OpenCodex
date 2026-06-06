using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Admin;

public sealed class AdminUserServiceTests
{
    [Fact]
    public void ListUsersReturnsRepositoryUsers()
    {
        var users = new FakeAdminUserRepository
        {
            Users = [new UserRecord("admin", "superadmin", true, 1, 2)]
        };
        var service = CreateService(users);

        var result = service.ListUsers();

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("admin", Assert.Single(result.Data).Username);
    }

    [Fact]
    public void CreateUserTrimsInputAndPassesEnabledFlag()
    {
        var users = new FakeAdminUserRepository
        {
            CreatedUser = new UserRecord("alice", "user", false, 1, 2)
        };
        var service = CreateService(users);

        var result = service.CreateUser(new AdminUserCreateCommand(
            " alice ",
            " alice-pw ",
            false));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("alice", result.Data.Username);
        Assert.Equal([("alice", "alice-pw", false)], users.CreateUserCalls);
    }

    [Fact]
    public void UpdateUserReturnsNotFoundWhenRepositoryCannotFindUser()
    {
        var users = new FakeAdminUserRepository();
        var service = CreateService(users);

        var result = service.UpdateUser("missing", new AdminUserUpdateCommand(null, null));

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Equal(AdminUserErrorCodes.NotFound, result.Code);
        Assert.Equal("user not found", result.Message);
    }

    [Fact]
    public void UpdateUserRejectsEnvironmentSuperadminPasswordReset()
    {
        var users = new FakeAdminUserRepository
        {
            User = new UserRecord("admin", "superadmin", true, 1, 2)
        };
        var service = CreateService(users);

        var result = service.UpdateUser("admin", new AdminUserUpdateCommand(null, "new-pw"));

        Assert.False(result.Succeeded);
        Assert.Equal(AdminUserErrorCodes.Validation, result.Code);
        Assert.Equal("environment superadmin password is managed by env", result.Message);
        Assert.Empty(users.ResetPasswordCalls);
    }

    [Fact]
    public void UpdateUserSetsEnabledWhenCommandHasEnabled()
    {
        var users = new FakeAdminUserRepository();
        var service = CreateService(users);

        var result = service.UpdateUser("alice", new AdminUserUpdateCommand(false, null));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.False(result.Data.Enabled);
        Assert.Equal([("alice", false, "admin")], users.SetEnabledCalls);
        Assert.Empty(users.ResetPasswordCalls);
    }

    [Fact]
    public void UpdateUserTrimsPasswordAndSkipsEnabledWhenCommandEnabledIsMissing()
    {
        var users = new FakeAdminUserRepository
        {
            User = new UserRecord("alice", "user", true, 1, 2)
        };
        var service = CreateService(users);

        var result = service.UpdateUser("alice", new AdminUserUpdateCommand(null, " new-pw "));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal([("alice", "new-pw")], users.ResetPasswordCalls);
        Assert.Empty(users.SetEnabledCalls);
    }

    [Fact]
    public void DeleteUserMapsMissingUserToNotFound()
    {
        var users = new FakeAdminUserRepository
        {
            DeleteUserException = new InvalidOperationException("user not found")
        };
        var service = CreateService(users);

        var result = service.DeleteUser("missing", "admin");

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Equal(AdminUserErrorCodes.NotFound, result.Code);
        Assert.Equal("user not found", result.Message);
    }

    private static AdminUserService CreateService(FakeAdminUserRepository users)
    {
        return new AdminUserService(
            new FakeSettingsProvider(new OpenCodexRuntimeSettings("test.db", "admin", "admin-pw", 120)),
            users);
    }

    private sealed class FakeSettingsProvider : IOpenCodexRuntimeSettingsProvider
    {
        private readonly OpenCodexRuntimeSettings _settings;

        public FakeSettingsProvider(OpenCodexRuntimeSettings settings)
        {
            _settings = settings;
        }

        public OpenCodexRuntimeSettings GetSettings()
        {
            return _settings;
        }
    }

    private sealed class FakeAdminUserRepository : IAdminUserRepository
    {
        public IReadOnlyList<UserRecord> Users { get; init; } = [];

        public UserRecord? AuthenticatedUser { get; init; }

        public UserRecord? CreatedUser { get; init; }

        public UserRecord? User { get; init; }

        public InvalidOperationException? DeleteUserException { get; init; }

        public List<(string Username, string Password, bool Enabled)> CreateUserCalls { get; } = [];

        public List<(string Username, string Password)> ResetPasswordCalls { get; } = [];

        public List<(string Username, bool Enabled, string ProtectedUsername)> SetEnabledCalls { get; } = [];

        public UserRecord EnsureSuperadmin(string username, string password)
        {
            return new UserRecord(username, "superadmin", true, 1, 2);
        }

        public UserRecord? AuthenticateUser(string username, string password)
        {
            return AuthenticatedUser;
        }

        public IReadOnlyList<UserRecord> ListUsers()
        {
            return Users;
        }

        public UserRecord CreateUser(string username, string password, bool enabled)
        {
            CreateUserCalls.Add((username, password, enabled));
            return CreatedUser ?? new UserRecord(username, "user", enabled, 1, 2);
        }

        public UserRecord? GetUser(string username)
        {
            return User;
        }

        public UserRecord SetUserEnabled(string username, bool enabled, string protectedUsername)
        {
            SetEnabledCalls.Add((username, enabled, protectedUsername));
            return new UserRecord(username, "user", enabled, 1, 2);
        }

        public UserRecord ResetUserPassword(string username, string password)
        {
            ResetPasswordCalls.Add((username, password));
            return new UserRecord(username, "user", true, 1, 2);
        }

        public UserRecord DeleteUser(string username, string protectedUsername)
        {
            if (DeleteUserException is not null)
            {
                throw DeleteUserException;
            }

            return new UserRecord(username, "user", true, 1, 2);
        }
    }
}

using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Admin;

public sealed class AdminAuthServiceTests
{
    [Fact]
    public void LoginBootstrapsConfiguredSuperadminAndUsesDefaultUsername()
    {
        var users = new FakeAdminUserRepository
        {
            AuthenticatedUser = new UserRecord("admin", "superadmin", true, 1, 2)
        };
        var service = new AdminAuthService(
            new FakeSettingsProvider(new OpenCodexRuntimeSettings("test.db", "admin", "pw", 120)),
            users);

        var result = service.Login("", "pw");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("admin", result.Data.Username);
        Assert.Equal([("admin", "pw")], users.EnsureSuperadminCalls);
        Assert.Equal([("admin", "pw")], users.AuthenticateUserCalls);
    }

    [Fact]
    public void LoginTrimsCredentialsAndReturnsBusinessFailureForInvalidCredentials()
    {
        var users = new FakeAdminUserRepository();
        var service = new AdminAuthService(
            new FakeSettingsProvider(new OpenCodexRuntimeSettings("test.db", "root", "root-pw", 120)),
            users);

        var result = service.Login(" alice ", " bad ");

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Equal(AdminAuthService.InvalidCredentialsCode, result.Code);
        Assert.Equal("用户名或密码错误", result.Message);
        Assert.Equal([("root", "root-pw")], users.EnsureSuperadminCalls);
        Assert.Equal([("alice", "bad")], users.AuthenticateUserCalls);
    }

    [Fact]
    public void LoginSkipsSuperadminBootstrapWhenAdminPasswordIsMissing()
    {
        var users = new FakeAdminUserRepository
        {
            AuthenticatedUser = new UserRecord("alice", "user", true, 1, 2)
        };
        var service = new AdminAuthService(
            new FakeSettingsProvider(new OpenCodexRuntimeSettings("test.db", "admin", "", 120)),
            users);

        var result = service.Login("alice", "alice-pw");

        Assert.True(result.Succeeded);
        Assert.Empty(users.EnsureSuperadminCalls);
        Assert.Equal([("alice", "alice-pw")], users.AuthenticateUserCalls);
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
        public List<(string Username, string Password)> EnsureSuperadminCalls { get; } = [];

        public List<(string Username, string Password)> AuthenticateUserCalls { get; } = [];

        public UserRecord? AuthenticatedUser { get; init; }

        public UserRecord EnsureSuperadmin(string username, string password)
        {
            EnsureSuperadminCalls.Add((username, password));
            return new UserRecord(username, "superadmin", true, 1, 2);
        }

        public UserRecord? AuthenticateUser(string username, string password)
        {
            AuthenticateUserCalls.Add((username, password));
            return AuthenticatedUser;
        }

        public IReadOnlyList<UserRecord> ListUsers()
        {
            throw new NotSupportedException();
        }

        public UserRecord CreateUser(string username, string password, bool enabled)
        {
            throw new NotSupportedException();
        }

        public UserRecord? GetUser(string username)
        {
            throw new NotSupportedException();
        }

        public UserRecord SetUserEnabled(string username, bool enabled, string protectedUsername)
        {
            throw new NotSupportedException();
        }

        public UserRecord ResetUserPassword(string username, string password)
        {
            throw new NotSupportedException();
        }

        public UserRecord DeleteUser(string username, string protectedUsername)
        {
            throw new NotSupportedException();
        }
    }
}

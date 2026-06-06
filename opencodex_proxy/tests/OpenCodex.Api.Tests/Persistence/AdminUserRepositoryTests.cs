using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;

namespace OpenCodex.Api.Tests.Persistence;

public sealed class AdminUserRepositoryTests
{
    [Fact]
    public void ListUsersUsesGenericUserRepositoryReadOrder()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.EnsureSuperadmin(workspace.DatabasePath, "root", "root-pw");
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "bob", "bob-pw");
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "alice", "alice-pw");
        var repository = CreateRepository(workspace.DatabasePath, "root");

        var users = repository.ListUsers();

        Assert.Equal(["root", "alice", "bob"], users.Select(user => user.Username));
        Assert.Equal("superadmin", users[0].Role);
    }

    [Fact]
    public void GetUserUsesGenericUserRepository()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "alice", "alice-pw");
        var repository = CreateRepository(workspace.DatabasePath);

        var user = repository.GetUser("alice");

        Assert.NotNull(user);
        Assert.Equal("alice", user.Username);
        Assert.Equal("user", user.Role);
        Assert.True(user.Enabled);
    }

    private static AdminUserRepository CreateRepository(
        string databasePath,
        string adminUsername = "admin")
    {
        var settingsProvider = new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(databasePath, adminUsername, "pw", 120));
        return new AdminUserRepository(
            settingsProvider,
            new SqliteRepository<UserEntity>(settingsProvider));
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

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"opencodex-admin-user-repository-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            DatabasePath = System.IO.Path.Combine(Path, "test.db");
        }

        public string Path { get; }

        public string DatabasePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

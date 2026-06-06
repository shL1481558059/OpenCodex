using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;

namespace OpenCodex.Api.Tests.Persistence;

public sealed class AdminApiKeyRepositoryTests
{
    [Fact]
    public void ListAccessApiKeysWithoutOwnerUsesGenericRepository()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "bob", "bob-pw");
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "alice", "alice-pw");
        var bobFirst = OpenCodexDatabase.CreateAccessApiKey(workspace.DatabasePath, "bob", "First");
        var alice = OpenCodexDatabase.CreateAccessApiKey(workspace.DatabasePath, "alice", "Laptop");
        var bobSecond = OpenCodexDatabase.CreateAccessApiKey(workspace.DatabasePath, "bob", "Second");
        var repository = CreateRepository(workspace.DatabasePath);

        var keys = repository.ListAccessApiKeys(ownerUsername: null);

        Assert.Equal([alice.Id, bobSecond.Id, bobFirst.Id], keys.Select(key => key.Id));
        Assert.Equal(["alice", "bob", "bob"], keys.Select(key => key.OwnerUsername));
        Assert.All(keys, key => Assert.NotNull(key.Key));
    }

    [Fact]
    public void ListAccessApiKeysWithOwnerPreservesOwnerScopedQuery()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "bob", "bob-pw");
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "alice", "alice-pw");
        OpenCodexDatabase.CreateAccessApiKey(workspace.DatabasePath, "bob", "Bob");
        var alice = OpenCodexDatabase.CreateAccessApiKey(workspace.DatabasePath, "alice", "Laptop");
        var repository = CreateRepository(workspace.DatabasePath);

        var keys = repository.ListAccessApiKeys(ownerUsername: "alice");

        var key = Assert.Single(keys);
        Assert.Equal(alice.Id, key.Id);
        Assert.Equal("alice", key.OwnerUsername);
        Assert.Equal(alice.Key, key.Key);
    }

    private static AdminApiKeyRepository CreateRepository(
        string databasePath,
        string adminUsername = "admin")
    {
        var settingsProvider = new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(databasePath, adminUsername, "pw", 120));
        return new AdminApiKeyRepository(
            settingsProvider,
            new SqliteRepository<AccessApiKeyEntity>(settingsProvider));
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
                $"opencodex-admin-api-key-repository-{Guid.NewGuid():N}");
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

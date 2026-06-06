using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;

namespace OpenCodex.Api.Tests.Persistence;

public sealed class AdminConfigRepositoryTests
{
    [Fact]
    public void ReadChannelsWithoutOwnerUsesGenericRepository()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.ReplaceChannels(
            workspace.DatabasePath,
            [
                Channel("bob-first", "https://bob-first.example.test/v1"),
                Channel("bob-second", "https://bob-second.example.test/v1")
            ],
            ownerUsername: "bob");
        OpenCodexDatabase.ReplaceChannels(
            workspace.DatabasePath,
            [Channel("alice-chat", "https://alice.example.test/v1")],
            ownerUsername: "alice");
        var repository = CreateRepository(workspace.DatabasePath);

        var channels = repository.ReadChannels(ownerUsername: null);

        Assert.Equal(["alice-chat", "bob-first", "bob-second"], channels.Select(channel => channel.Id));
        Assert.Equal(["alice", "bob", "bob"], channels.Select(channel => channel.OwnerUsername));
    }

    [Fact]
    public void ReadChannelsWithOwnerPreservesOwnerScopedQuery()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.ReplaceChannels(
            workspace.DatabasePath,
            [Channel("bob-chat", "https://bob.example.test/v1")],
            ownerUsername: "bob");
        OpenCodexDatabase.ReplaceChannels(
            workspace.DatabasePath,
            [Channel("alice-chat", "https://alice.example.test/v1")],
            ownerUsername: "alice");
        var repository = CreateRepository(workspace.DatabasePath);

        var channels = repository.ReadChannels(ownerUsername: "alice");

        var channel = Assert.Single(channels);
        Assert.Equal("alice", channel.OwnerUsername);
        Assert.Equal("alice-chat", channel.Id);
    }

    private static AdminConfigRepository CreateRepository(
        string databasePath,
        string adminUsername = "admin")
    {
        var settingsProvider = new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(databasePath, adminUsername, "pw", 120));
        return new AdminConfigRepository(
            settingsProvider,
            new SqliteRepository<ChannelEntity>(settingsProvider));
    }

    private static Dictionary<string, object?> Channel(string id, string baseUrl)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = id,
            ["type"] = "chat",
            ["baseurl"] = baseUrl,
            ["apikey"] = "secret",
            ["auth_mode"] = "config",
            ["headers"] = new Dictionary<string, object?>(),
            ["timeout_seconds"] = 30,
            ["retry_count"] = 2,
            ["compat"] = new Dictionary<string, object?>(),
            ["models"] = new List<object?>(),
            ["enabled"] = true
        };
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
                $"opencodex-admin-config-repository-{Guid.NewGuid():N}");
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

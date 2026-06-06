using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;

namespace OpenCodex.Api.Tests.Persistence;

public sealed class SqliteRepositoryTests
{
    [Fact]
    public void GetByIdLoadsUserEntity()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "alice", "alice-pw");
        var repository = new SqliteRepository<UserEntity>(new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(workspace.DatabasePath, "admin", "pw", 120)));

        var user = repository.GetById("alice");

        Assert.NotNull(user);
        Assert.Equal("alice", user.Username);
        Assert.Equal("user", user.Role);
        Assert.True(user.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(user.PasswordHash));
    }

    [Fact]
    public void GetByIdReturnsNullForBlankUserId()
    {
        using var workspace = new TempWorkspace();
        var repository = new SqliteRepository<UserEntity>(new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(workspace.DatabasePath, "admin", "pw", 120)));

        var user = repository.GetById(" ");

        Assert.Null(user);
    }

    [Fact]
    public void ListAllLoadsUsersInDatabaseOrder()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.EnsureSuperadmin(workspace.DatabasePath, "root", "root-pw");
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "bob", "bob-pw");
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "alice", "alice-pw");
        var repository = new SqliteRepository<UserEntity>(new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(workspace.DatabasePath, "root", "pw", 120)));

        var users = repository.ListAll();

        Assert.Equal(["root", "alice", "bob"], users.Select(user => user.Username));
        Assert.Equal("superadmin", users[0].Role);
        Assert.All(users, user => Assert.False(string.IsNullOrWhiteSpace(user.PasswordHash)));
    }

    [Fact]
    public void GetByIdLoadsAccessApiKeyEntity()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "alice", "alice-pw");
        var created = OpenCodexDatabase.CreateAccessApiKey(workspace.DatabasePath, "alice", "Laptop");
        var repository = new SqliteRepository<AccessApiKeyEntity>(new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(workspace.DatabasePath, "admin", "pw", 120)));

        var key = repository.GetById(created.Id);

        Assert.NotNull(key);
        Assert.Equal(created.Id, key.Id);
        Assert.Equal("alice", key.OwnerUsername);
        Assert.Equal("Laptop", key.Name);
        Assert.Equal(created.KeyPrefix, key.KeyPrefix);
        Assert.Equal(created.KeySuffix, key.KeySuffix);
        Assert.Equal(created.Key, key.KeyPlaintext);
        Assert.False(string.IsNullOrWhiteSpace(key.KeyHash));
    }

    [Fact]
    public void ListAllLoadsAccessApiKeysInAdminListOrder()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "bob", "bob-pw");
        OpenCodexDatabase.CreateUser(workspace.DatabasePath, "alice", "alice-pw");
        var bobFirst = OpenCodexDatabase.CreateAccessApiKey(workspace.DatabasePath, "bob", "First");
        var alice = OpenCodexDatabase.CreateAccessApiKey(workspace.DatabasePath, "alice", "Laptop");
        var bobSecond = OpenCodexDatabase.CreateAccessApiKey(workspace.DatabasePath, "bob", "Second");
        var repository = new SqliteRepository<AccessApiKeyEntity>(new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(workspace.DatabasePath, "admin", "pw", 120)));

        var keys = repository.ListAll();

        Assert.Equal([alice.Id, bobSecond.Id, bobFirst.Id], keys.Select(key => key.Id));
        Assert.Equal(["alice", "bob", "bob"], keys.Select(key => key.OwnerUsername));
        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(key.KeyHash)));
    }

    [Fact]
    public void GetByIdLoadsChannelEntity()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.ReplaceChannels(
            workspace.DatabasePath,
            [
                Channel(
                    "chat",
                    "https://alice.example.test/v1",
                    headers: new Dictionary<string, object?> { ["X-Test"] = "yes" },
                    compat: new Dictionary<string, object?> { ["drop_params"] = new List<object?> { "store" } },
                    models: [new Dictionary<string, object?> { ["model"] = "gpt-5" }])
            ],
            ownerUsername: "alice");
        var repository = new SqliteRepository<ChannelEntity>(new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(workspace.DatabasePath, "admin", "pw", 120)));

        var channel = repository.GetById(("alice", "chat"));

        Assert.NotNull(channel);
        Assert.Equal(("alice", "chat"), channel.GetId());
        Assert.Equal("https://alice.example.test/v1", channel.BaseUrl);
        Assert.Equal("yes", channel.Headers["X-Test"]);
        Assert.Equal(["store"], Assert.IsType<List<object?>>(channel.Compat["drop_params"]));
        Assert.Equal("gpt-5", Assert.IsType<Dictionary<string, object?>>(Assert.Single(channel.Models))["model"]);
    }

    [Fact]
    public void ListAllLoadsChannelsInAdminConfigOrder()
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
        var repository = new SqliteRepository<ChannelEntity>(new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(workspace.DatabasePath, "admin", "pw", 120)));

        var channels = repository.ListAll();

        Assert.Equal(["alice-chat", "bob-first", "bob-second"], channels.Select(channel => channel.Id));
        Assert.Equal(["alice", "bob", "bob"], channels.Select(channel => channel.OwnerUsername));
        Assert.Equal([0, 0, 1], channels.Select(channel => channel.Position));
    }

    [Fact]
    public void GetByIdLoadsWebSearchSettingsEntity()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.ReplaceWebSearchConfig(
            workspace.DatabasePath,
            new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["key_usage_limit"] = 77,
                ["keys"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["key"] = "tvly-1",
                        ["enabled"] = true
                    }
                }
            });
        var repository = new SqliteRepository<WebSearchSettingsEntity>(new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(workspace.DatabasePath, "admin", "pw", 120)));

        var settings = repository.GetById(1);

        Assert.NotNull(settings);
        Assert.Equal(1L, settings.Id);
        Assert.True(settings.Enabled);
        Assert.Equal(77, settings.KeyUsageLimit);
    }

    [Fact]
    public void ListAllLoadsTavilyKeysInAdminOrder()
    {
        using var workspace = new TempWorkspace();
        var saved = OpenCodexDatabase.ReplaceWebSearchConfig(
            workspace.DatabasePath,
            new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["keys"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["key"] = "tvly-first",
                        ["enabled"] = true
                    },
                    new Dictionary<string, object?>
                    {
                        ["key"] = "tvly-second",
                        ["enabled"] = false
                    }
                }
            });
        var repository = new SqliteRepository<TavilyKeyEntity>(new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(workspace.DatabasePath, "admin", "pw", 120)));

        var keys = repository.ListAll();

        Assert.Equal(["tvly-first", "tvly-second"], keys.Select(key => key.Key));
        Assert.Equal(saved.Keys.Select(key => key.Id), keys.Select(key => key.Id));
        Assert.Equal([0, 1], keys.Select(key => key.Position));
        Assert.Equal([true, false], keys.Select(key => key.Enabled));
    }

    private static Dictionary<string, object?> Channel(
        string id,
        string baseUrl,
        IReadOnlyDictionary<string, object?>? headers = null,
        IReadOnlyDictionary<string, object?>? compat = null,
        IReadOnlyList<object?>? models = null)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = id,
            ["type"] = "chat",
            ["baseurl"] = baseUrl,
            ["apikey"] = "secret",
            ["auth_mode"] = "config",
            ["headers"] = headers ?? new Dictionary<string, object?>(),
            ["timeout_seconds"] = 30,
            ["retry_count"] = 2,
            ["compat"] = compat ?? new Dictionary<string, object?>(),
            ["models"] = models ?? [],
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
                $"opencodex-sqlite-repository-{Guid.NewGuid():N}");
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

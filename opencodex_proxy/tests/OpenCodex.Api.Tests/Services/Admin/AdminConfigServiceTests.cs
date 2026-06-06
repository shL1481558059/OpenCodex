using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Admin;

public sealed class AdminConfigServiceTests
{
    [Fact]
    public void ReadConfigScopesRegularUserToSelf()
    {
        var repository = new FakeAdminConfigRepository
        {
            Channels = [Channel("alice", "chat")]
        };
        var service = CreateService(repository);

        var result = service.ReadConfig("alice", isSuperadmin: false);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("chat", Assert.Single(result.Data).Id);
        Assert.Equal(["alice"], repository.ReadOwnerCalls);
    }

    [Fact]
    public void SaveConfigForcesRegularUserOwnerToSelf()
    {
        var repository = new FakeAdminConfigRepository
        {
            Channels = [Channel("alice", "chat")]
        };
        var service = CreateService(repository);

        var result = service.SaveConfig(
            new Dictionary<string, object?>
            {
                ["channels"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["owner_username"] = "admin",
                        ["id"] = "chat",
                        ["type"] = "chat",
                        ["baseurl"] = "https://alice.example.test/v1"
                    }
                }
            },
            "alice",
            isSuperadmin: false);

        Assert.True(result.Succeeded);
        var saved = Assert.Single(repository.ReplaceCalls);
        Assert.Equal("alice", saved.OwnerUsername);
        var channel = Assert.Single(saved.Channels);
        Assert.Equal("alice", channel["owner_username"]);
    }

    [Fact]
    public void SaveConfigForSuperadminDefaultsBlankOwnerToConfiguredAdmin()
    {
        var repository = new FakeAdminConfigRepository
        {
            Channels = [Channel("admin", "chat")]
        };
        var service = CreateService(repository);

        var result = service.SaveConfig(
            new Dictionary<string, object?>
            {
                ["channels"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = "chat",
                        ["type"] = "chat",
                        ["baseurl"] = "https://admin.example.test/v1"
                    }
                }
            },
            "admin",
            isSuperadmin: true);

        Assert.True(result.Succeeded);
        var saved = Assert.Single(repository.ReplaceCalls);
        Assert.Null(saved.OwnerUsername);
        Assert.Equal("admin", Assert.Single(saved.Channels)["owner_username"]);
    }

    [Fact]
    public void SaveConfigMapsValidationFailureToBusinessResult()
    {
        var repository = new FakeAdminConfigRepository();
        var service = CreateService(repository);

        var result = service.SaveConfig(
            new Dictionary<string, object?>
            {
                ["channels"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = "bad",
                        ["type"] = "chat"
                    }
                }
            },
            "admin",
            isSuperadmin: true);

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Equal(AdminConfigErrorCodes.Validation, result.Code);
        Assert.Contains("baseurl", result.Message, StringComparison.Ordinal);
        Assert.Empty(repository.ReplaceCalls);
    }

    [Fact]
    public void ImportConfigAppendsNewChannelsAndSkipsDuplicateIds()
    {
        var repository = new FakeAdminConfigRepository
        {
            Channels = [Channel("admin", "chat")]
        };
        var service = CreateService(repository);

        var result = service.ImportConfig(
            new Dictionary<string, object?>
            {
                ["channels"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = "chat",
                        ["type"] = "chat",
                        ["baseurl"] = "https://duplicate.example.test/v1"
                    },
                    new Dictionary<string, object?>
                    {
                        ["id"] = "responses",
                        ["type"] = "responses",
                        ["baseurl"] = "https://responses.example.test/v1"
                    }
                }
            },
            "admin",
            isSuperadmin: true);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(1, result.Data.Imported);
        Assert.Equal(1, result.Data.Skipped);
        Assert.Equal(["chat"], result.Data.SkippedIds);
        var saved = Assert.Single(repository.ReplaceCalls);
        Assert.Equal(["chat", "responses"], saved.Channels.Select(channel => channel["id"]).ToArray());
    }

    [Fact]
    public void ImportConfigRejectsNonListChannels()
    {
        var repository = new FakeAdminConfigRepository();
        var service = CreateService(repository);

        var result = service.ImportConfig(
            new Dictionary<string, object?> { ["channels"] = "not-list" },
            "admin",
            isSuperadmin: true);

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Equal(AdminConfigErrorCodes.Validation, result.Code);
        Assert.Equal("channels must be a list", result.Message);
        Assert.Empty(repository.ReplaceCalls);
    }

    private static AdminConfigService CreateService(FakeAdminConfigRepository repository)
    {
        return new AdminConfigService(
            new FakeSettingsProvider(new OpenCodexRuntimeSettings("test.db", "admin", "admin-pw", 120)),
            repository);
    }

    private static ChannelRecord Channel(string owner, string id)
    {
        return new ChannelRecord(
            owner,
            id,
            id,
            "chat",
            $"https://{id}.example.test/v1",
            "secret",
            "config",
            new Dictionary<string, object?>(StringComparer.Ordinal),
            120,
            3,
            new Dictionary<string, object?>(StringComparer.Ordinal),
            [],
            true);
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

    private sealed class FakeAdminConfigRepository : IAdminConfigRepository
    {
        public IReadOnlyList<ChannelRecord> Channels { get; init; } = [];

        public List<string?> ReadOwnerCalls { get; } = [];

        public List<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Channels, string? OwnerUsername)> ReplaceCalls { get; } = [];

        public IReadOnlyList<ChannelRecord> ReadChannels(string? ownerUsername)
        {
            ReadOwnerCalls.Add(ownerUsername);
            return Channels;
        }

        public void ReplaceChannels(
            IEnumerable<IReadOnlyDictionary<string, object?>> channels,
            string? ownerUsername)
        {
            ReplaceCalls.Add((
                channels.Select(channel => channel.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal)).ToList(),
                ownerUsername));
        }
    }
}

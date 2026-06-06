using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;

namespace OpenCodex.Api.Tests.Persistence;

public sealed class AdminWebSearchRepositoryTests
{
    [Fact]
    public void ReadWebSearchConfigUsesGenericRepositoryForSettingsAndKeys()
    {
        using var workspace = new TempWorkspace();
        OpenCodexDatabase.ReplaceWebSearchConfig(
            workspace.DatabasePath,
            new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["key_usage_limit"] = 42,
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
        var repository = CreateRepository(workspace.DatabasePath);

        var config = repository.ReadWebSearchConfig();

        Assert.True(config.Enabled);
        Assert.Equal(["tavily"], config.Providers);
        Assert.Equal(1000, config.DefaultKeyUsageLimit);
        Assert.Equal(["tvly-first", "tvly-second"], config.Keys.Select(key => key.Key));
        Assert.Equal([0, 1], config.Keys.Select(key => key.Position));
    }

    [Fact]
    public void ReadWebSearchConfigDefaultsWhenSettingsRowIsMissing()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace.DatabasePath);

        var config = repository.ReadWebSearchConfig();

        Assert.False(config.Enabled);
        Assert.Empty(config.Keys);
    }

    private static AdminWebSearchRepository CreateRepository(string databasePath)
    {
        var settingsProvider = new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(databasePath, "admin", "pw", 120));
        return new AdminWebSearchRepository(
            settingsProvider,
            new SqliteRepository<WebSearchSettingsEntity>(settingsProvider),
            new SqliteRepository<TavilyKeyEntity>(settingsProvider));
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
                $"opencodex-admin-web-search-repository-{Guid.NewGuid():N}");
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

using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;

namespace OpenCodex.Api.Tests.Persistence;

public sealed class ProxyWebSearchRepositoryTests
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
                ["keys"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["key"] = "tvly-first",
                        ["enabled"] = true
                    }
                }
            });
        var repository = CreateRepository(workspace.DatabasePath);

        var config = repository.ReadWebSearchConfig();

        Assert.True(config.Enabled);
        Assert.Equal(["tvly-first"], config.Keys.Select(key => key.Key));
    }

    private static ProxyWebSearchRepository CreateRepository(string databasePath)
    {
        var settingsProvider = new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(databasePath, "admin", "pw", 120));
        return new ProxyWebSearchRepository(
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
                $"opencodex-proxy-web-search-repository-{Guid.NewGuid():N}");
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

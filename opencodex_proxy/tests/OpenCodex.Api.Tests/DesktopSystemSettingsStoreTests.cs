using Microsoft.Extensions.Configuration;
using OpenCodex.Api.Configuration;
using OpenCodex.CoreBase.DTOs.SystemSettings;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class DesktopSystemSettingsStoreTests
{
    [Fact]
    public void SaveAndGet_RoundTripsAccessModeAndPort()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            "settings",
            $"{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var store = CreateStore(path);
        var draft = store.Normalize(new SystemSettingsUpdateRequest
        {
            AccessMode = "localhost",
            Port = 18080
        });
        store.Save(draft);

        var loaded = CreateStore(path).Get();

        Assert.Equal("localhost", loaded.AccessMode);
        Assert.Equal(18080, loaded.Port);
    }

    [Fact]
    public void Save_DoesNotWriteProbeInterceptionField()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            "settings",
            $"{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var store = CreateStore(path);
        var draft = store.Normalize(new SystemSettingsUpdateRequest
        {
            AccessMode = "localhost",
            Port = 18080
        });
        store.Save(draft);

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("intercept_probe_requests", raw);
    }

    private static DesktopSystemSettingsStore CreateStore(string path)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENCODEX_DESKTOP_SETTINGS_PATH"] = path
            })
            .Build();
        return new DesktopSystemSettingsStore(configuration);
    }
}

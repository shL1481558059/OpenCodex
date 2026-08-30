using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxySettingsServiceTests
{
    [Fact]
    public async Task SetThenGet_RoundTripsBool()
    {
        var (service, context) = CreateFixture();

        var result = await service.SetAsync("intercept_probe_requests", "true");

        Assert.True(result.Succeeded);
        Assert.True(service.GetBool("intercept_probe_requests", false));
        var row = context.ProxySettings.Single();
        Assert.Equal("intercept_probe_requests", row.Key);
        Assert.Equal("true", row.Value);
    }

    [Fact]
    public async Task SetExistingKey_UpdatesInsteadOfInserting()
    {
        var (service, context) = CreateFixture();

        await service.SetAsync("intercept_probe_requests", "true");
        var firstId = context.ProxySettings.Single().Id;
        await service.SetAsync("intercept_probe_requests", "false");

        Assert.Single(context.ProxySettings);
        Assert.Equal(firstId, context.ProxySettings.Single().Id);
        Assert.False(service.GetBool("intercept_probe_requests", true));
    }

    [Fact]
    public void GetMissingKey_ReturnsFallback()
    {
        var (service, _) = CreateFixture();

        Assert.False(service.GetBool("missing", false));
        Assert.True(service.GetBool("missing", true));
    }

    [Fact]
    public async Task SetEmptyKey_Fails()
    {
        var (service, _) = CreateFixture();

        var result = await service.SetAsync("", "true");

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Code);
    }

    [Fact]
    public async Task GetAll_ReturnsAllStoredKeys()
    {
        var (service, _) = CreateFixture();

        await service.SetAsync("intercept_probe_requests", "true");
        await service.SetAsync("another_flag", "1");
        var all = await service.GetAllAsync();

        Assert.True(all.Succeeded);
        Assert.Equal("true", all.Payload!["intercept_probe_requests"]);
        Assert.Equal("1", all.Payload!["another_flag"]);
    }

    private static (ProxySettingsService Service, IOpenCodexDbContext Context) CreateFixture()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-proxy-settings-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var seed = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            seed.Database.Migrate();
        }

        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var service = new ProxySettingsService(new EfRepository<ProxySetting>(context));
        return (service, context);
    }
}

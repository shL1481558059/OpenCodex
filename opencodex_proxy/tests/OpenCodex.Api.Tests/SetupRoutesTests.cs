using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class SetupRoutesTests
{
    [Fact]
    public async Task SetupStatusRequiresSetupWhenNoUsersAndNoEnvironmentSuperadmin()
    {
        using var factory = new SetupApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/setup/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("Data");
        Assert.True(data.GetProperty("setup_required").GetBoolean());
        Assert.False(data.GetProperty("has_users").GetBoolean());
        Assert.False(data.GetProperty("environment_superadmin_configured").GetBoolean());
    }

    [Fact]
    public async Task SetupCreatesSuperadminAndRejectsRepeatSetup()
    {
        using var factory = new SetupApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var first = await client.PostAsJsonAsync("/setup", new
        {
            username = "owner",
            password = "secret-password",
            system_settings = new
            {
                access_mode = "localhost",
                port = 18080
            }
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/setup", new
        {
            username = "owner2",
            password = "secret-password",
            system_settings = new
            {
                access_mode = "localhost",
                port = 18080
            }
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var login = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "owner",
            ["password"] = "secret-password"
        }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("Data");
        Assert.True(data.GetProperty("authenticated").GetBoolean());
        Assert.Equal("owner", data.GetProperty("user").GetProperty("username").GetString());
    }
}

public sealed class SetupApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        "opencodex-api-tests",
        $"{Guid.NewGuid():N}.db");
    private readonly string _dataProtectionKeysPath = Path.Combine(
        Path.GetTempPath(),
        "opencodex-api-tests",
        "keys",
        Guid.NewGuid().ToString("N"));
    private readonly string _desktopSettingsPath = Path.Combine(
        Path.GetTempPath(),
        "opencodex-api-tests",
        "settings",
        $"{Guid.NewGuid():N}.json");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            Directory.CreateDirectory(_dataProtectionKeysPath);
            Directory.CreateDirectory(Path.GetDirectoryName(_desktopSettingsPath)!);
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENCODEX_DISABLE_DOTENV"] = "true",
                ["OPENCODEX_DB_PROVIDER"] = "sqlite",
                ["OPENCODEX_DB_CONNECTION_STRING"] = $"Data Source={_dbPath}",
                ["OPENCODEX_DEFAULT_TIMEOUT"] = "120",
                ["OPENCODEX_DATA_PROTECTION_KEYS_PATH"] = _dataProtectionKeysPath,
                ["OPENCODEX_DESKTOP_SETTINGS_PATH"] = _desktopSettingsPath
            });
        });
    }
}

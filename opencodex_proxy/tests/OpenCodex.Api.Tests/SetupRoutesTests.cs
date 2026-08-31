using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Services;
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

        using (var setupDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
        {
            var sync = setupDocument.RootElement.GetProperty("Data").GetProperty("model_catalog_sync");
            Assert.Equal("completed", sync.GetProperty("status").GetString());
            Assert.Equal(1, sync.GetProperty("result").GetProperty("models").GetProperty("created").GetInt32());
        }
        using (var context = OpenCodex.Data.OpenCodexDbContextFactory.Create(
            "sqlite",
            $"Data Source={factory.DbPath}"))
        {
            var model = Assert.Single(context.ModelInfos);
            Assert.Equal("setup-model", model.ModelKey);
            Assert.Equal("sync", model.Source);
        }

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
        using var loginDocument = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = loginDocument.RootElement.GetProperty("Data");
        Assert.True(data.GetProperty("authenticated").GetBoolean());
        Assert.Equal("owner", data.GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public async Task SetupSucceedsWhenModelCatalogSyncFails()
    {
        using var factory = new SetupApiFactory(new SetupModelCatalogSyncClient(
            new InvalidOperationException("remote unavailable")));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var response = await client.PostAsJsonAsync("/setup", new
        {
            username = "owner",
            password = "secret-password",
            system_settings = new
            {
                access_mode = "localhost",
                port = 18080
            }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("Data");
        Assert.Equal("failed", data.GetProperty("model_catalog_sync").GetProperty("status").GetString());
        Assert.Equal("owner", data.GetProperty("session").GetProperty("user").GetProperty("username").GetString());

        using var context = OpenCodex.Data.OpenCodexDbContextFactory.Create(
            "sqlite",
            $"Data Source={factory.DbPath}");
        Assert.Single(context.Users);
        Assert.Empty(context.ModelInfos);
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

    public SetupApiFactory(IModelCatalogSyncClient? modelCatalogSync = null)
    {
        ModelCatalogSync = modelCatalogSync ?? new SetupModelCatalogSyncClient();
    }

    public string DbPath => _dbPath;

    public IModelCatalogSyncClient ModelCatalogSync { get; }

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
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IModelCatalogSyncClient>();
            services.AddSingleton(ModelCatalogSync);
        });
    }
}

public sealed class SetupModelCatalogSyncClient : IModelCatalogSyncClient
{
    private readonly Exception? _failure;

    public SetupModelCatalogSyncClient(Exception? failure = null)
    {
        _failure = failure;
    }

    public string? LastUrl { get; private set; }

    public Task<ModelCatalogTransferDocument> FetchAsync(string url)
    {
        LastUrl = url;
        if (_failure is not null)
        {
            throw _failure;
        }

        return Task.FromResult(new ModelCatalogTransferDocument
        {
            Type = "model_catalog",
            Version = 1,
            ExportedAt = "2026-08-31T00:00:00Z",
            Providers =
            [
                new ModelCatalogProviderTransfer
                {
                    Code = "setup-provider",
                    Name = "Setup Provider",
                    Enabled = true,
                    SortOrder = 10
                }
            ],
            Models =
            [
                new ModelCatalogModelTransfer
                {
                    ProviderCode = "setup-provider",
                    ModelKey = "setup-model",
                    DisplayName = "Setup Model",
                    MatchType = "exact",
                    MatchPattern = "setup-model",
                    Enabled = true
                }
            ]
        });
    }
}

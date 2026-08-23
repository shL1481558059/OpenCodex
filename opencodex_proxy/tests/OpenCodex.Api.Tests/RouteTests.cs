using System.Net;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class RouteTests : IClassFixture<OpenCodexApiFactory>
{
    private readonly HttpClient _client;

    public RouteTests(OpenCodexApiFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
    }

    [Fact]
    public void ControllerRoutesDoNotUseAdminApiPrefix()
    {
        var routes = typeof(Program).Assembly
            .GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .Select(attribute => attribute.Template)
            .Where(template => !string.IsNullOrEmpty(template))
            .ToArray();

        Assert.DoesNotContain(routes, route => route!.Contains("/admin/api", StringComparison.Ordinal));
        Assert.Contains("/session", routes);
        Assert.Contains("/login", routes);
        Assert.Contains("/config", routes);
        Assert.Contains("/channels", routes);
        Assert.Contains("/channels/{channelId:guid}", routes);
        Assert.Contains("/channels/runtime", routes);
        Assert.Contains("/channels/bulk-import", routes);
        Assert.Contains("/channels/{channelId:guid}/health-reset", routes);
        Assert.Contains("/model-catalog/export", routes);
        Assert.Contains("/model-catalog/import", routes);
        Assert.Contains("/logs", routes);
    }


    [Fact]
    public async Task FreshStartupDoesNotSeedModelCatalogOrLegacyPricing()
    {
        using var factory = new OpenCodexApiFactory();
        using var client = factory.CreateClient();
        var session = await client.GetAsync("/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OpenCodex.CoreBase.Data.IOpenCodexDbContext>();
        Assert.Empty(context.ModelProviders);
        Assert.Empty(context.ModelInfos);
        Assert.Empty(context.ModelPricingPlans);
        Assert.Empty(context.ModelPricingRules);
    }

    [Fact]
    public async Task StartupKeepsExistingCatalogAndDoesNotMigrateLegacyPricing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "opencodex-api-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(testRoot, "catalog.db");
        var keysPath = Path.Combine(testRoot, "keys");
        Directory.CreateDirectory(testRoot);

        var providerId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        using (var context = OpenCodex.Data.OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            context.ModelProviders.Add(new OpenCodex.Core.Domain.ModelProvider
            {
                Id = providerId,
                Code = "manual-provider",
                Name = "Manual Provider",
                Enabled = true,
                SortOrder = 10,
                Source = "manual",
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.ModelInfos.Add(new OpenCodex.Core.Domain.ModelInfo
            {
                Id = modelId,
                Scope = "global",
                ProviderId = providerId,
                ModelKey = "manual-model",
                DisplayName = "Manual Model",
                MatchType = "exact",
                MatchPattern = "manual-model",
                CatalogJson = "{}",
                CapabilitiesJson = "{}",
                Enabled = true,
                Source = "manual",
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.ModelPricingPlans.Add(new OpenCodex.Core.Domain.ModelPricingPlan
            {
                Id = planId,
                ModelInfoId = modelId,
                Currency = "USD",
                Enabled = true,
                Source = "manual",
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.ModelPricingRules.Add(new OpenCodex.Core.Domain.ModelPricingRule
            {
                Id = Guid.NewGuid(),
                PricingPlanId = planId,
                BillingItem = "input",
                BillingMode = "per_million_tokens",
                UnitPrice = 2,
                TiersJson = "[]",
                Enabled = true
            });
            context.SaveChanges();
        }

        using var factory = new OpenCodexApiFactory(dbPath, keysPath);
        using var client = factory.CreateClient();
        var session = await client.GetAsync("/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        using var verify = OpenCodex.Data.OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        Assert.Contains(verify.ModelProviders, provider => provider.Id == providerId);
        Assert.Contains(verify.ModelInfos, model => model.Id == modelId);
        Assert.Contains(verify.ModelPricingPlans, plan => plan.Id == planId);
        Assert.Contains(verify.ModelPricingRules, rule => rule.PricingPlanId == planId);
        Assert.DoesNotContain(verify.ModelInfos, model => model.ModelKey == "legacy-only-model");
    }

    [Fact]
    public async Task NewAdminRoutesAreAvailable()
    {
        var session = await _client.GetAsync("/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        var cookie = await LoginAndReadSessionCookie();

        var config = await SendWithCookie(HttpMethod.Get, "/config", cookie);
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);

        var logs = await SendWithCookie(HttpMethod.Get, "/logs?page=1&page_size=5", cookie);
        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
    }

    [Fact]
    public async Task ConfigEndpoint_ReturnsCurrentChannelCapacityUsage()
    {
        using var factory = new OpenCodexApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var cookie = await LoginAndReadSessionCookie(client, "admin", OpenCodexApiFactory.AdminPassword);

        var config = await CreateChannelAsync(client, cookie, new
        {
            id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            name = "Chat",
            type = "chat",
            baseurl = "https://example.test/v1",
            apikey = "secret",
            auth_mode = "config",
            timeout_seconds = 30,
            circuit_break_duration_seconds = 30,
            retry_count = 0,
            priority = 2,
            capacity = 3,
            enabled = true,
            models = new[]
            {
                new { model = "public-model", upstream_model = "upstream-model" }
            }
        });
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);

        using var scope = factory.Services.CreateScope();
        var channelCapacity = scope.ServiceProvider.GetRequiredService<IChannelCapacityService>();
        using var lease = await channelCapacity.TryAcquireAsync(
            "admin",
            new Dictionary<string, object?>
            {
                ["id"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ["capacity"] = 3
            });

        var response = await SendWithCookie(client, HttpMethod.Get, "/config", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var channel = document.RootElement.GetProperty("Data").GetProperty("channels")[0];
        Assert.Equal(2, channel.GetProperty("priority").GetInt32());
        Assert.Equal(3, channel.GetProperty("capacity").GetInt32());
        Assert.Equal(1, channel.GetProperty("active_requests").GetInt32());
        Assert.Equal("healthy", channel.GetProperty("health_status").GetString());
    }

    [Fact]
    public async Task ConfigEndpoint_ReturnsOpenHealthStatusWhenCircuitIsOpen()
    {
        using var factory = new OpenCodexApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var cookie = await LoginAndReadSessionCookie(client, "admin", OpenCodexApiFactory.AdminPassword);

        var config = await CreateChannelAsync(client, cookie, new
        {
            id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            name = "Chat",
            type = "chat",
            baseurl = "https://example.test/v1",
            apikey = "secret",
            auth_mode = "config",
            timeout_seconds = 30,
            circuit_break_duration_seconds = 30,
            retry_count = 0,
            priority = 2,
            capacity = 3,
            enabled = true,
            models = new[]
            {
                new { model = "public-model", upstream_model = "upstream-model" }
            }
        });
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
           var breaker = scope.ServiceProvider.GetRequiredService<IChannelCircuitBreakerService>();
            await breaker.RecordFailureAsync("admin", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new UpstreamException("down", ProxyHttpStatus.BadGateway));
            await breaker.RecordFailureAsync("admin", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new UpstreamException("down", ProxyHttpStatus.BadGateway));
            await breaker.RecordFailureAsync("admin", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new UpstreamException("down", ProxyHttpStatus.BadGateway));
        }

        var response = await SendWithCookie(client, HttpMethod.Get, "/config", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var channel = document.RootElement.GetProperty("Data").GetProperty("channels")[0];
        Assert.Equal("open", channel.GetProperty("health_status").GetString());
    }

    [Fact]
    public async Task ResetChannelHealthEndpoint_ClearsOpenCircuit()
    {
        using var factory = new OpenCodexApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var cookie = await LoginAndReadSessionCookie(client, "admin", OpenCodexApiFactory.AdminPassword);

        var config = await CreateChannelAsync(client, cookie, new
        {
            id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            name = "Chat",
            type = "chat",
            baseurl = "https://example.test/v1",
            apikey = "secret",
            auth_mode = "config",
            timeout_seconds = 30,
            retry_count = 0,
            priority = 2,
            capacity = 3,
            enabled = true,
            models = new[]
            {
                new { model = "public-model", upstream_model = "upstream-model" }
            }
        });
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
           var breaker = scope.ServiceProvider.GetRequiredService<IChannelCircuitBreakerService>();
            await breaker.RecordFailureAsync("admin", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new UpstreamException("down", ProxyHttpStatus.BadGateway));
            await breaker.RecordFailureAsync("admin", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new UpstreamException("down", ProxyHttpStatus.BadGateway));
            await breaker.RecordFailureAsync("admin", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new UpstreamException("down", ProxyHttpStatus.BadGateway));
        }

        var reset = await SendWithCookie(
            client,
            HttpMethod.Post,
            "/channels/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/reset-health",
            cookie);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var response = await SendWithCookie(client, HttpMethod.Get, "/config", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var channel = document.RootElement.GetProperty("Data").GetProperty("channels")[0];
        Assert.Equal("healthy", channel.GetProperty("health_status").GetString());
    }

    [Fact]
    public async Task UpdateChannel_OnlyTouchesTargetChannel()
    {
        using var factory = new OpenCodexApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var cookie = await LoginAndReadSessionCookie(client, "admin", OpenCodexApiFactory.AdminPassword);
        var firstId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var secondId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var firstCreate = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/channels",
            cookie,
            new
            {
                id = firstId,
                name = "First",
                type = "chat",
                baseurl = "https://example.test/v1",
                apikey = "secret",
                auth_mode = "config",
                timeout_seconds = 30,
                retry_count = 0,
                priority = 2,
                capacity = 3,
                enabled = true,
                models = new[]
                {
                    new { model = "first-model", upstream_model = "first-upstream" }
                }
            });
        Assert.Equal(HttpStatusCode.OK, firstCreate.StatusCode);

        var secondCreate = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/channels",
            cookie,
            new
            {
                id = secondId,
                name = "Second",
                type = "chat",
                baseurl = "https://example.test/v1",
                apikey = "secret",
                auth_mode = "config",
                timeout_seconds = 30,
                retry_count = 0,
                priority = 3,
                capacity = 3,
                enabled = true,
                models = new[]
                {
                    new { model = "second-model", upstream_model = "second-upstream" }
                }
            });
        Assert.Equal(HttpStatusCode.OK, secondCreate.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var connectionString = configuration["OPENCODEX_DB_CONNECTION_STRING"] ?? throw new InvalidOperationException("Missing test DB connection string");
            using var context = OpenCodex.Data.OpenCodexDbContextFactory.Create("sqlite", connectionString);
            context.Database.Migrate();

            var firstChannel = await context.Channels.SingleAsync(channel => channel.Id == firstId);
            var secondChannel = await context.Channels.SingleAsync(channel => channel.Id == secondId);
            firstChannel.UpdatedAt = 100;
            secondChannel.UpdatedAt = 200;
            await context.SaveChangesAsync();
        }

        var update = await SendJsonWithCookie(
            client,
            HttpMethod.Put,
            $"/channels/{firstId}",
            cookie,
            new
            {
                id = secondId,
                name = "First Updated",
                type = "chat",
                baseurl = "https://example.test/v1",
                apikey = "secret",
                auth_mode = "config",
                timeout_seconds = 45,
                retry_count = 1,
                priority = 5,
                capacity = 3,
                enabled = false,
                models = new[]
                {
                    new { model = "first-model", upstream_model = "first-upstream" }
                }
            });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var connectionString = configuration["OPENCODEX_DB_CONNECTION_STRING"] ?? throw new InvalidOperationException("Missing test DB connection string");
            using var context = OpenCodex.Data.OpenCodexDbContextFactory.Create("sqlite", connectionString);
            context.Database.Migrate();

            var channels = await context.Channels.OrderBy(channel => channel.Name).ToListAsync();
            Assert.Equal(2, channels.Count);

            var updatedChannel = channels.Single(channel => channel.Id == firstId);
            var untouchedChannel = channels.Single(channel => channel.Id == secondId);

            Assert.Equal("First Updated", updatedChannel.Name);
            Assert.False(updatedChannel.Enabled);
            Assert.True(updatedChannel.UpdatedAt > 200);
            Assert.Equal(200, untouchedChannel.UpdatedAt);
        }
    }

    [Fact]
    public async Task UpdateChannel_PreservesExistingGroupWhenRequestOmitsGroupName()
    {
        using var factory = new OpenCodexApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var cookie = await LoginAndReadSessionCookie(client, "admin", OpenCodexApiFactory.AdminPassword);
        var channelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var create = await CreateChannelAsync(client, cookie, new
        {
            id = channelId,
            name = "Grouped",
            group_name = "Primary",
            type = "chat",
            baseurl = "https://example.test/v1",
            apikey = "secret",
            auth_mode = "config",
            timeout_seconds = 30,
            retry_count = 0,
            priority = 2,
            capacity = 3,
            enabled = true,
            models = new[]
            {
                new { model = "grouped-model", upstream_model = "grouped-upstream" }
            }
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var update = await SendJsonWithCookie(
            client,
            HttpMethod.Put,
            $"/channels/{channelId}",
            cookie,
            new
            {
                id = channelId,
                name = "Grouped Updated",
                type = "chat",
                baseurl = "https://example.test/v1",
                apikey = "secret",
                auth_mode = "config",
                timeout_seconds = 45,
                retry_count = 1,
                priority = 3,
                capacity = 4,
                enabled = true,
                models = new[]
                {
                    new { model = "grouped-model", upstream_model = "grouped-upstream" }
                }
            });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using var document = await JsonDocument.ParseAsync(await update.Content.ReadAsStreamAsync());
        var channel = document.RootElement.GetProperty("Data");
        Assert.Equal("Grouped Updated", channel.GetProperty("name").GetString());
        Assert.Equal("Primary", channel.GetProperty("group_name").GetString());
    }

    [Fact]
    public async Task BatchUpdateChannels_PatchesOnlySelectedChannels()
    {
        using var factory = new OpenCodexApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var cookie = await LoginAndReadSessionCookie(client, "admin", OpenCodexApiFactory.AdminPassword);
        var firstId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var secondId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var firstCreate = await CreateChannelAsync(client, cookie, new
        {
            id = firstId,
            name = "Batch First",
            type = "chat",
            baseurl = "https://example.test/v1",
            apikey = "secret",
            auth_mode = "config",
            timeout_seconds = 30,
            retry_count = 0,
            priority = 2,
            capacity = 3,
            enabled = true,
            models = new[] { new { model = "first-model", upstream_model = "first-upstream" } }
        });
        Assert.Equal(HttpStatusCode.OK, firstCreate.StatusCode);

        var secondCreate = await CreateChannelAsync(client, cookie, new
        {
            id = secondId,
            name = "Batch Second",
            group_name = "Unchanged",
            type = "responses",
            baseurl = "https://example.test/v2",
            apikey = "secret-2",
            auth_mode = "config",
            timeout_seconds = 60,
            retry_count = 2,
            priority = 4,
            capacity = 5,
            enabled = true,
            models = new[] { new { model = "second-model", upstream_model = "second-upstream" } }
        });
        Assert.Equal(HttpStatusCode.OK, secondCreate.StatusCode);

        var batch = await SendJsonWithCookie(
            client,
            HttpMethod.Patch,
            "/channels/batch",
            cookie,
            new
            {
                channel_ids = new[] { firstId },
                patch = new
                {
                    group_name = "Base URL A",
                    enabled = false,
                    priority = 9,
                    capacity = 6,
                    timeout_seconds = 90,
                    retry_count = 1,
                    circuit_break_duration_seconds = 30
                }
            });
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);

        // 批量更新返回 { updated_ids, count }，不再返回渠道列表。通过 GET /config 验证。
        using var batchDocument = await JsonDocument.ParseAsync(await batch.Content.ReadAsStreamAsync());
        var updatedIds = batchDocument.RootElement.GetProperty("Data").GetProperty("updated_ids").EnumerateArray()
            .Select(id => id.GetGuid()).ToList();
        Assert.Single(updatedIds);
        Assert.Equal(firstId, updatedIds[0]);

        var config = await SendWithCookie(client, HttpMethod.Get, "/config", cookie);
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        using var document = await JsonDocument.ParseAsync(await config.Content.ReadAsStreamAsync());
        var channels = document.RootElement.GetProperty("Data").GetProperty("channels").EnumerateArray().ToList();
        var first = channels.Single(item => item.GetProperty("id").GetString() == firstId.ToString());
        var second = channels.Single(item => item.GetProperty("id").GetString() == secondId.ToString());

        Assert.Equal("Base URL A", first.GetProperty("group_name").GetString());
        Assert.False(first.GetProperty("enabled").GetBoolean());
        Assert.Equal(9, first.GetProperty("priority").GetInt32());
        Assert.Equal(6, first.GetProperty("capacity").GetInt32());
        Assert.Equal(90, first.GetProperty("timeout_seconds").GetInt32());
        Assert.Equal(1, first.GetProperty("retry_count").GetInt32());
        Assert.Equal(30, first.GetProperty("circuit_break_duration_seconds").GetInt32());

        Assert.Equal("Unchanged", second.GetProperty("group_name").GetString());
        Assert.True(second.GetProperty("enabled").GetBoolean());
        Assert.Equal(4, second.GetProperty("priority").GetInt32());
        Assert.Equal(5, second.GetProperty("capacity").GetInt32());
        Assert.Equal(60, second.GetProperty("timeout_seconds").GetInt32());
        Assert.Equal(2, second.GetProperty("retry_count").GetInt32());
    }

    [Fact]
    public async Task CreateChannel_RejectsDuplicateNameForSameOwner()
    {
        using var factory = new OpenCodexApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var cookie = await LoginAndReadSessionCookie(client, "admin", OpenCodexApiFactory.AdminPassword);

        var first = await CreateChannelAsync(client, cookie, new
        {
            id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            name = "Duplicated",
            type = "chat",
            baseurl = "https://example.test/v1",
            apikey = "secret",
            auth_mode = "config",
            timeout_seconds = 30,
            retry_count = 0,
            priority = 1,
            capacity = 3,
            enabled = true,
            models = new[] { new { model = "m1", upstream_model = "u1" } }
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await CreateChannelAsync(client, cookie, new
        {
            id = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            name = "Duplicated",
            type = "chat",
            baseurl = "https://example.test/v1",
            apikey = "secret",
            auth_mode = "config",
            timeout_seconds = 30,
            retry_count = 0,
            priority = 2,
            capacity = 3,
            enabled = true,
            models = new[] { new { model = "m2", upstream_model = "u2" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task UpdateChannel_UsesPathIdAndKeepsOwner()
    {
        using var factory = new OpenCodexApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var adminCookie = await LoginAndReadSessionCookie(client, "admin", OpenCodexApiFactory.AdminPassword);

        var createdUser = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/users",
            adminCookie,
            new
            {
                username = "worker",
                password = "worker-password",
                enabled = true
            });
        Assert.Equal(HttpStatusCode.Created, createdUser.StatusCode);

        var workerCookie = await LoginAndReadSessionCookie(client, "worker", "worker-password");
        var create = await CreateChannelAsync(client, workerCookie, new
        {
            id = "cccccccc-cccc-cccc-cccc-cccccccccccc",
            name = "Worker Channel",
            type = "chat",
            baseurl = "https://example.test/v1",
            apikey = "secret",
            auth_mode = "config",
            timeout_seconds = 30,
            retry_count = 0,
            priority = 1,
            capacity = 3,
            enabled = true,
            models = new[] { new { model = "m1", upstream_model = "u1" } }
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var update = await SendJsonWithCookie(
            client,
            HttpMethod.Put,
            "/channels/cccccccc-cccc-cccc-cccc-cccccccccccc",
            adminCookie,
            new
            {
                id = "dddddddd-dddd-dddd-dddd-dddddddddddd",
                owner_username = "admin",
                name = "Worker Channel Updated",
                type = "chat",
                baseurl = "https://example.test/v2",
                apikey = "secret-2",
                auth_mode = "config",
                timeout_seconds = 45,
                retry_count = 1,
                priority = 5,
                capacity = 4,
                enabled = false,
                models = new[] { new { model = "m1", upstream_model = "u1" } }
            });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var config = await SendWithCookie(client, HttpMethod.Get, "/config", adminCookie);
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        using var document = await JsonDocument.ParseAsync(await config.Content.ReadAsStreamAsync());
        var channels = document.RootElement.GetProperty("Data").GetProperty("channels");
        var channel = channels.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "cccccccc-cccc-cccc-cccc-cccccccccccc");
        Assert.Equal("worker", channel.GetProperty("owner_username").GetString());
        Assert.Equal("Worker Channel Updated", channel.GetProperty("name").GetString());
        Assert.False(channel.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task ConfigSave_BackfillsHistoricalNullCapacityToThreeAndRejectsNewNullCapacity()
    {
        using var factory = new OpenCodexApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var cookie = await LoginAndReadSessionCookie(client, "admin", OpenCodexApiFactory.AdminPassword);

        const string legacyChannelId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaabbbb";
        var initialSave = await CreateChannelAsync(client, cookie, new
        {
            id = legacyChannelId,
            name = "Legacy",
            type = "chat",
            baseurl = "https://example.test/v1",
            apikey = "secret",
            auth_mode = "config",
            timeout_seconds = 30,
            retry_count = 0,
            priority = 0,
            enabled = true,
            models = new[]
            {
                new { model = "legacy-model", upstream_model = "legacy-upstream" }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, initialSave.StatusCode);

        await SeedHistoricalNullCapacityChannel(factory.Services, legacyChannelId);

        var rejectMissingCapacityForHistoricalChannel = await SendJsonWithCookie(
            client,
            HttpMethod.Put,
            $"/channels/{legacyChannelId}",
            cookie,
            new
            {
                id = legacyChannelId,
                name = "Legacy Updated",
                type = "chat",
                baseurl = "https://example.test/v1",
                apikey = "secret",
                auth_mode = "config",
                timeout_seconds = 45,
                retry_count = 1,
                priority = 0,
                enabled = true,
                models = new[]
                {
                    new { model = "legacy-model", upstream_model = "legacy-upstream" }
                }
            });
        Assert.Equal(HttpStatusCode.BadRequest, rejectMissingCapacityForHistoricalChannel.StatusCode);

        var preserveBackfilledCapacity = await SendJsonWithCookie(
            client,
            HttpMethod.Put,
            $"/channels/{legacyChannelId}",
            cookie,
            new
            {
                id = legacyChannelId,
                name = "Legacy Updated",
                type = "chat",
                baseurl = "https://example.test/v1",
                apikey = "secret",
                auth_mode = "config",
                timeout_seconds = 45,
                retry_count = 1,
                priority = 0,
                capacity = 3,
                enabled = true,
                models = new[]
                {
                    new { model = "legacy-model", upstream_model = "legacy-upstream" }
                }
            });
        Assert.Equal(HttpStatusCode.OK, preserveBackfilledCapacity.StatusCode);

        var config = await SendWithCookie(client, HttpMethod.Get, "/config", cookie);
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        using var document = await JsonDocument.ParseAsync(await config.Content.ReadAsStreamAsync());
        var channel = document.RootElement.GetProperty("Data").GetProperty("channels")[0];
        Assert.Equal(3, channel.GetProperty("capacity").GetInt32());
        Assert.Equal("Legacy Updated", channel.GetProperty("name").GetString());
    }

    [Fact]
    public async Task OldAdminApiRoutesAreNotAvailable()
    {
        var login = await _client.PostAsync(
            "/admin/api/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = OpenCodexApiFactory.AdminPassword
            }));
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);

        var session = await _client.GetAsync("/admin/api/session");
        Assert.Equal(HttpStatusCode.NotFound, session.StatusCode);

        var cookie = await LoginAndReadSessionCookie();

        var config = await SendWithCookie(HttpMethod.Get, "/admin/api/config", cookie);
        Assert.Equal(HttpStatusCode.NotFound, config.StatusCode);

        var logs = await SendWithCookie(HttpMethod.Get, "/admin/api/logs?page=1&page_size=5", cookie);
        Assert.Equal(HttpStatusCode.NotFound, logs.StatusCode);
    }

    [Fact]
    public async Task FailedApiResponsesUseHttpStatusCodeInResponseCode()
    {
        var login = await _client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = "wrong-password"
            }));
        await AssertResponseCode(login, HttpStatusCode.Unauthorized);

        var cookie = await LoginAndReadSessionCookie();
        var log = await SendWithCookie(HttpMethod.Get, "/logs/00000000-0000-0000-0000-000000000001", cookie);
        await AssertResponseCode(log, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ModelCatalogImportExportRequireSuperadmin()
    {
        var adminCookie = await LoginAndReadSessionCookie();
        var username = $"catalog-user-{Guid.NewGuid():N}";
        var password = "user-password";
        var createdUser = await SendJsonWithCookie(
            HttpMethod.Post,
            "/users",
            adminCookie,
            new
            {
                username,
                password,
                enabled = true
            });
        Assert.Equal(HttpStatusCode.Created, createdUser.StatusCode);

        var userCookie = await LoginAndReadSessionCookie(username, password);
        var exported = await SendWithCookie(HttpMethod.Get, "/model-catalog/export", userCookie);
        await AssertResponseCode(exported, HttpStatusCode.Forbidden);

        using var importContent = new StringContent(
            JsonSerializer.Serialize(new
            {
                type = "model_catalog",
                version = 1,
                exported_at = "2026-08-17T12:00:00Z",
                providers = new object[] { },
                models = new object[] { }
            }),
            System.Text.Encoding.UTF8,
            "application/json");
        using var importRequest = new HttpRequestMessage(HttpMethod.Post, "/model-catalog/import?dryRun=true")
        {
            Content = importContent
        };
        importRequest.Headers.Add("Cookie", userCookie);
        var imported = await _client.SendAsync(importRequest);
        await AssertResponseCode(imported, HttpStatusCode.Forbidden);

        var adminExport = await SendWithCookie(HttpMethod.Get, "/model-catalog/export", adminCookie);
        Assert.Equal(HttpStatusCode.OK, adminExport.StatusCode);
        Assert.Equal(
            "application/json",
            adminExport.Content.Headers.ContentType?.MediaType,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginCookieIsPersistent()
    {
        var response = await Login("admin", OpenCodexApiFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(
            cookies,
            value => value.Contains("expires=", StringComparison.OrdinalIgnoreCase)
                || value.Contains("max-age=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoginCookieRemainsValidAfterApplicationRestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "opencodex-api-tests", $"{Guid.NewGuid():N}.db");
        var keyPath = Path.Combine(Path.GetTempPath(), "opencodex-api-tests", "keys", Guid.NewGuid().ToString("N"));

        string cookie;
        using (var firstFactory = new OpenCodexApiFactory(dbPath, keyPath))
        using (var firstClient = firstFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        }))
        {
            cookie = await LoginAndReadSessionCookie(firstClient, "admin", OpenCodexApiFactory.AdminPassword);
        }

        using var secondFactory = new OpenCodexApiFactory(dbPath, keyPath);
        using var secondClient = secondFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var request = new HttpRequestMessage(HttpMethod.Get, "/session");
        request.Headers.Add("Cookie", cookie);

        var response = await secondClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("Data").GetProperty("authenticated").GetBoolean());
    }

    private static async Task<Guid> ReadIdProperty(HttpResponseMessage response, params string[] path)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var element = document.RootElement;
        foreach (var key in path)
        {
            element = element.GetProperty(key);
        }
        var idStr = element.GetString();
        if (idStr is null || !Guid.TryParse(idStr, out var id))
        {
            throw new InvalidOperationException($"Invalid GUID at path {string.Join(".", path)}");
        }
        return id;
    }
    private async Task<string> LoginAndReadSessionCookie()
    {
        return await LoginAndReadSessionCookie(_client, "admin", OpenCodexApiFactory.AdminPassword);
    }

    private async Task<string> LoginAndReadSessionCookie(
        string username,
        string password)
    {
        return await LoginAndReadSessionCookie(_client, username, password);
    }

    private static async Task<string> LoginAndReadSessionCookie(
        HttpClient client,
        string username,
        string password)
    {
        var response = await Login(client, username, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));

        var cookie = cookies
            .Select(value => value.Split(';', 2)[0])
            .FirstOrDefault(value => value.Contains('='));

        Assert.False(string.IsNullOrEmpty(cookie));
        return cookie;
    }

    private Task<HttpResponseMessage> Login(
        string username,
        string password)
    {
        return Login(_client, username, password);
    }

    private static Task<HttpResponseMessage> Login(
        HttpClient client,
        string username,
        string password)
    {
        return client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password
            }));
    }

    private Task<HttpResponseMessage> SendJsonWithCookie(
        HttpMethod method,
        string requestUri,
        string cookie,
        object body)
    {
        return SendJsonWithCookie(_client, method, requestUri, cookie, body);
    }

    private static Task<HttpResponseMessage> CreateChannelAsync(
        HttpClient client,
        string cookie,
        object body)
    {
        return SendJsonWithCookie(client, HttpMethod.Post, "/channels", cookie, body);
    }

    private static Task<HttpResponseMessage> SendJsonWithCookie(
        HttpClient client,
        HttpMethod method,
        string requestUri,
        string cookie,
        object body)
    {
        var request = new HttpRequestMessage(method, requestUri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendWithCookie(
        HttpMethod method,
        string requestUri,
        string cookie)
    {
        return SendWithCookie(_client, method, requestUri, cookie);
    }

    private static Task<HttpResponseMessage> SendWithCookie(
        HttpClient client,
        HttpMethod method,
        string requestUri,
        string cookie)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static async Task AssertResponseCode(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            Assert.Fail($"Expected JSON response but got empty body for status {response.StatusCode}");
            return;
        }
        using var document = JsonDocument.Parse(body);
        Assert.Equal((int)expectedStatusCode, document.RootElement.GetProperty("ErrorCode").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("ErrorMsg", out _));    }

    private static async Task<long> ReadLongProperty(
        HttpResponseMessage response,
        params string[] path)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var element = document.RootElement;
        foreach (var segment in path)
        {
            element = element.GetProperty(segment);
        }

        return element.GetInt64();
    }

    private static async Task SeedHistoricalNullCapacityChannel(IServiceProvider services, string channelId)
    {
        using var scope = services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration["OPENCODEX_DB_CONNECTION_STRING"] ?? throw new InvalidOperationException("Missing test DB connection string");
        using var context = OpenCodex.Data.OpenCodexDbContextFactory.Create("sqlite", connectionString);
        context.Database.Migrate();
        context.Channels.Add(new OpenCodex.Core.Domain.Channel
        {
            OwnerUserId = Guid.NewGuid(),
            Id = Guid.Parse(channelId),
            Position = 0,
            Priority = 0,
            Name = "Legacy",
            Type = "chat",
            BaseUrl = "https://example.test/v1",
            ApiKey = "secret",
            AuthMode = "config",
            HeadersJson = "{}",
            TimeoutSeconds = 30,
            RetryCount = 0,
            Capacity = 3,
            CompatJson = "{}",
            ModelsJson = """[{"model":"legacy-model","upstream_model":"legacy-upstream","supports_image":false}]""",
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        await context.SaveChangesAsync();
    }
}

public sealed class OpenCodexApiFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "test-password";

    private readonly string _dbPath;
    private readonly string _dataProtectionKeysPath;

    public OpenCodexApiFactory()
        : this(
            Path.Combine(
                Path.GetTempPath(),
                "opencodex-api-tests",
                $"{Guid.NewGuid():N}.db"),
            Path.Combine(
                Path.GetTempPath(),
                "opencodex-api-tests",
                "keys",
                Guid.NewGuid().ToString("N")))
    {
    }

    internal OpenCodexApiFactory(
        string dbPath,
        string dataProtectionKeysPath)
    {
        _dbPath = dbPath;
        _dataProtectionKeysPath = dataProtectionKeysPath;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            Directory.CreateDirectory(_dataProtectionKeysPath);
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENCODEX_ADMIN_USERNAME"] = "admin",
                ["OPENCODEX_ADMIN_PASSWORD"] = AdminPassword,
                ["OPENCODEX_DB_PROVIDER"] = "sqlite",
                    ["OPENCODEX_DB_CONNECTION_STRING"] = $"Data Source={_dbPath}",
                ["OPENCODEX_DEFAULT_TIMEOUT"] = "120",
                ["OPENCODEX_DATA_PROTECTION_KEYS_PATH"] = _dataProtectionKeysPath
            });
        });
    }
}

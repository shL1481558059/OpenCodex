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
        Assert.Contains("/pricing", routes);
        Assert.Contains("/logs", routes);
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

        var pricing = await SendWithCookie(HttpMethod.Get, "/pricing", cookie);
        Assert.Equal(HttpStatusCode.OK, pricing.StatusCode);
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

        var config = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/config",
            cookie,
            new
            {
                channels = new[]
                {
                    new
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
                    }
                }
            });
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);

        using var scope = factory.Services.CreateScope();
        var channelCapacity = scope.ServiceProvider.GetRequiredService<IChannelCapacityService>();
        using var lease = channelCapacity.TryAcquire(
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

        var config = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/config",
            cookie,
            new
            {
                channels = new[]
                {
                    new
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
                    }
                }
            });
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var breaker = scope.ServiceProvider.GetRequiredService<IChannelCircuitBreakerService>();
            breaker.RecordFailure("admin", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new UpstreamException("down", ProxyHttpStatus.BadGateway));
            breaker.RecordFailure("admin", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new UpstreamException("down", ProxyHttpStatus.BadGateway));
            breaker.RecordFailure("admin", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new UpstreamException("down", ProxyHttpStatus.BadGateway));
        }

        var response = await SendWithCookie(client, HttpMethod.Get, "/config", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var channel = document.RootElement.GetProperty("Data").GetProperty("channels")[0];
        Assert.Equal("open", channel.GetProperty("health_status").GetString());
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

        var initialSave = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/config",
            cookie,
            new
            {
                channels = new[]
                {
                    new
                    {
                        id = "legacy-null-capacity",
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
                    }
                }
            });
        Assert.Equal(HttpStatusCode.BadRequest, initialSave.StatusCode);

        await SeedHistoricalNullCapacityChannel(factory.Services);

        var rejectMissingCapacityForHistoricalChannel = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/config",
            cookie,
            new
            {
                channels = new[]
                {
                    new
                    {
                        id = "legacy-null-capacity",
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
                    }
                }
            });
        Assert.Equal(HttpStatusCode.BadRequest, rejectMissingCapacityForHistoricalChannel.StatusCode);

        var preserveBackfilledCapacity = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/config",
            cookie,
            new
            {
                channels = new[]
                {
                    new
                    {
                        id = "legacy-null-capacity",
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
                    }
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
    public async Task PricingDefaultsAreSeededAndSuperadminCanMaintain()
    {
        var cookie = await LoginAndReadSessionCookie();
        var list = await SendWithCookie(HttpMethod.Get, "/pricing", cookie);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var document = await JsonDocument.ParseAsync(await list.Content.ReadAsStreamAsync()))
        {
            Assert.True(document.RootElement.GetProperty("Data").GetProperty("prices").GetArrayLength() > 0);
        }

        var modelId = $"test-model-{Guid.NewGuid():N}";
        var created = await SendJsonWithCookie(
            HttpMethod.Post,
            "/pricing",
            cookie,
            new
            {
                model_id = modelId,
                vendor = "test",
                name = "Test Model",
                input_price = 1.5,
                cached_input_price = 0.5,
                output_price = 3,
                enabled = true
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var priceId = await ReadIdProperty(created, "Data", "price", "id");
        var updated = await SendJsonWithCookie(
            HttpMethod.Patch,
            $"/pricing/{priceId}",
            cookie,
            new
            {
                cached_input_price = (double?)null,
                enabled = false
            });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using (var document = await JsonDocument.ParseAsync(await updated.Content.ReadAsStreamAsync()))
        {
            var price = document.RootElement.GetProperty("Data").GetProperty("price");
            Assert.False(price.GetProperty("enabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, price.GetProperty("cached_input_price").ValueKind);
        }

        var deleted = await SendWithCookie(HttpMethod.Delete, $"/pricing/{priceId}", cookie);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
    }

    [Fact]
    public async Task PricingRoutesRequireSuperadmin()
    {
        var adminCookie = await LoginAndReadSessionCookie();
        var username = $"pricing-user-{Guid.NewGuid():N}";
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
        var pricing = await SendWithCookie(HttpMethod.Get, "/pricing", userCookie);
        await AssertResponseCode(pricing, HttpStatusCode.Forbidden);
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

    private static async Task SeedHistoricalNullCapacityChannel(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration["OPENCODEX_DB_CONNECTION_STRING"] ?? throw new InvalidOperationException("Missing test DB connection string");
        using var context = OpenCodex.Data.OpenCodexDbContextFactory.Create("sqlite", connectionString);
        context.Database.Migrate();
        context.Channels.Add(new OpenCodex.Core.Domain.Channel
        {
            OwnerUserId = Guid.NewGuid(),
            Id = Guid.NewGuid(),
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

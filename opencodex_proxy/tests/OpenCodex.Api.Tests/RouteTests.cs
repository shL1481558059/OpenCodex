using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
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
        var log = await SendWithCookie(HttpMethod.Get, "/logs/999999", cookie);
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

        var priceId = await ReadLongProperty(created, "Data", "price", "id");
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

    private async Task<string> LoginAndReadSessionCookie()
    {
        return await LoginAndReadSessionCookie("admin", OpenCodexApiFactory.AdminPassword);
    }

    private async Task<string> LoginAndReadSessionCookie(
        string username,
        string password)
    {
        var response = await _client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));

        var cookie = cookies
            .Select(value => value.Split(';', 2)[0])
            .FirstOrDefault(value => value.StartsWith(".AspNetCore.Session=", StringComparison.Ordinal));

        Assert.False(string.IsNullOrEmpty(cookie));
        return cookie;
    }

    private Task<HttpResponseMessage> SendJsonWithCookie(
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
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendWithCookie(
        HttpMethod method,
        string requestUri,
        string cookie)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("Cookie", cookie);
        return _client.SendAsync(request);
    }

    private static async Task AssertResponseCode(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal((int)expectedStatusCode, document.RootElement.GetProperty("ErrorCode").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("ErrorMsg", out _));
    }

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
}

public sealed class OpenCodexApiFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "test-password";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        "opencodex-api-tests",
        $"{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENCODEX_ADMIN_USERNAME"] = "admin",
                ["OPENCODEX_ADMIN_PASSWORD"] = AdminPassword,
                ["OPENCODEX_DB_PATH"] = _dbPath,
                ["OPENCODEX_DEFAULT_TIMEOUT"] = "120"
            });
        });
    }
}

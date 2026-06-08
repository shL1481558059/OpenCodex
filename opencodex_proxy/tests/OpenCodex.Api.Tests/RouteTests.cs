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
            AllowAutoRedirect = false
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

    private async Task<string> LoginAndReadSessionCookie()
    {
        var response = await _client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = OpenCodexApiFactory.AdminPassword
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));

        var cookie = cookies
            .Select(value => value.Split(';', 2)[0])
            .FirstOrDefault(value => value.StartsWith(".AspNetCore.Session=", StringComparison.Ordinal));

        Assert.False(string.IsNullOrEmpty(cookie));
        return cookie;
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
        Assert.False(document.RootElement.TryGetProperty("traceId", out _));
        Assert.False(document.RootElement.TryGetProperty("TraceId", out _));
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

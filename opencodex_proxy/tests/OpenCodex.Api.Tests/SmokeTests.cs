using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenCodex.Api.Tests;

public sealed class SmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SmokeTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RootRedirectsToAdmin()
    {
        using var client = CreateClient(allowAutoRedirect: false);
        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task HealthReturnsOk()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(payload);
        Assert.Equal("ok", payload["status"]);
    }

    [Fact]
    public async Task SwaggerDocumentIsAvailableInDevelopment()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.True(payload.TryGetProperty("openapi", out _));
        var info = payload.GetProperty("info");
        Assert.Equal("OpenCodex Proxy API", info.GetProperty("title").GetString());
        Assert.Equal("v1", info.GetProperty("version").GetString());
        Assert.Contains(
            "OpenAI-compatible proxy endpoints",
            info.GetProperty("description").GetString(),
            StringComparison.Ordinal);
        AssertSwaggerPath(payload, "/v1/responses", "post");
        AssertSwaggerPath(payload, "/v1/chat/completions", "post");
        AssertSwaggerPath(payload, "/v1/messages", "post");
        AssertSwaggerPath(payload, "/admin/api/channels/test", "post");
        AssertSwaggerPath(payload, "/admin/api/test-channel", "post");
        AssertSwaggerPath(payload, "/admin/api/channels/discover-models", "post");
        AssertSwaggerPath(payload, "/admin/api/discover-models", "post");
        AssertSwaggerResponseSchema(payload, "/admin/api/session", "get", "200", "AdminSessionResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/login", "post", "200", "AdminSessionResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/login", "post", "401", "AdminLoginErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/logout", "post", "200", "AdminSessionResponse");
        AssertSwaggerResponseSchema(payload, "/admin/logout", "post", "200", "AdminSessionResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/logs", "get", "200", "LogsPageResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/logs/{logId}", "get", "200", "LogDetailResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/logs/{logId}", "get", "404", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/stats", "get", "200", "StatsResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/users", "get", "200", "UsersResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/users", "post", "201", "UserResponsePayload");
        AssertSwaggerResponseSchema(payload, "/admin/api/users", "post", "400", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/users/{username}", "patch", "200", "UserResponsePayload");
        AssertSwaggerResponseSchema(payload, "/admin/api/users/{username}", "patch", "400", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/users/{username}", "patch", "404", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/users/{username}", "delete", "200", "DeleteUserResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/users/{username}", "delete", "400", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/users/{username}", "delete", "404", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/api-keys", "get", "200", "ApiKeysResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/api-keys", "post", "201", "ApiKeyResponsePayload");
        AssertSwaggerResponseSchema(payload, "/admin/api/api-keys", "post", "400", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/api-keys/{keyId}", "patch", "200", "ApiKeyResponsePayload");
        AssertSwaggerResponseSchema(payload, "/admin/api/api-keys/{keyId}", "patch", "400", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/api-keys/{keyId}", "patch", "404", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/api-keys/{keyId}", "delete", "200", "DeleteApiKeyResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/api-keys/{keyId}", "delete", "400", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/api-keys/{keyId}", "delete", "404", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/config", "get", "200", "ConfigResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/config/export", "get", "200", "ConfigResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/config/import", "post", "200", "ConfigImportResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/config/import", "post", "400", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/config", "post", "200", "ConfigResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/config", "post", "400", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/web-search", "get", "200", "WebSearchConfigResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/web-search", "post", "200", "WebSearchConfigResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/web-search", "post", "400", "AdminErrorResponse");
        AssertSwaggerResponseSchema(payload, "/admin/api/web-search/test-key", "post", "200", "WebSearchTestKeyResponsePayload");
        AssertSwaggerResponseSchema(payload, "/admin/api/web-search/test-key", "post", "400", "AdminErrorResponse");
        AssertSwaggerPath(payload, "/health", "get");
    }

    [Fact]
    public async Task SwaggerDocumentIsHiddenOutsideDevelopment()
    {
        using var client = CreateClient(environment: "Production");
        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminReturnsLoginFallbackWhenSpaIsMissing()
    {
        using var workspace = new TempWorkspace();
        using var client = CreateClient(adminStaticPath: workspace.StaticPath);

        var response = await client.GetAsync("/admin");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("OpenCodex Admin", html, StringComparison.Ordinal);
        Assert.Contains("管理密码", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminFormLoginRedirectsAndLogoutAliasClearsSession()
    {
        using var workspace = new TempWorkspace();
        using var client = CreateClient(
            allowAutoRedirect: false,
            dbPath: workspace.DatabasePath,
            adminStaticPath: workspace.StaticPath);

        var failedResponse = await client.PostAsync(
            "/admin",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["password"] = "wrong"
            }));
        var failedHtml = await failedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, failedResponse.StatusCode);
        Assert.Contains("用户名或密码错误", failedHtml, StringComparison.Ordinal);

        var loginResponse = await client.PostAsync(
            "/admin",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["password"] = "pw"
            }));
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/admin", loginResponse.Headers.Location?.ToString());

        CopySessionCookie(client, loginResponse);
        var session = await client.GetFromJsonAsync<JsonElement>("/admin/api/session");
        Assert.True(session.GetProperty("authenticated").GetBoolean());

        var logout = await client.PostAsync("/admin/logout", content: null);
        var logoutPayload = await logout.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.False(logoutPayload.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task AdminServesSpaIndexAssetsAndFallbackRoutes()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.StaticPath, "index.html"),
            "<!doctype html><html><body><div id=\"app\">spa</div></body></html>");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.StaticPath, "app.js"),
            "console.log('spa');");
        using var client = CreateClient(adminStaticPath: workspace.StaticPath);

        var admin = await client.GetAsync("/admin");
        var asset = await client.GetAsync("/admin/app.js");
        var fallback = await client.GetAsync("/admin/channels");

        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Contains("spa", await admin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("text/javascript", asset.Content.Headers.ContentType?.MediaType);
        Assert.Contains("console.log", await asset.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
        Assert.Contains("spa", await fallback.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private HttpClient CreateClient(
        bool allowAutoRedirect = true,
        string? dbPath = null,
        string? adminStaticPath = null,
        string environment = "Development")
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("environment", environment);
            builder.UseSetting("OpenCodex:AdminUsername", "admin");
            builder.UseSetting("OpenCodex:AdminPassword", "pw");
            if (dbPath is not null)
            {
                builder.UseSetting("OpenCodex:DbPath", dbPath);
            }

            if (adminStaticPath is not null)
            {
                builder.UseSetting("OpenCodex:AdminStaticPath", adminStaticPath);
            }
        }).CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = allowAutoRedirect
        });
    }

    private static void CopySessionCookie(HttpClient client, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return;
        }

        var cookieHeader = string.Join("; ", setCookies.Select(cookie => cookie.Split(';', 2)[0]));
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookieHeader);
    }

    private static void AssertSwaggerPath(JsonElement document, string path, string method)
    {
        Assert.True(
            document.GetProperty("paths").TryGetProperty(path, out var pathDocument),
            $"Swagger document should include path '{path}'.");
        Assert.True(
            pathDocument.TryGetProperty(method, out _),
            $"Swagger path '{path}' should include method '{method}'.");
    }

    private static void AssertSwaggerResponseSchema(
        JsonElement document,
        string path,
        string method,
        string statusCode,
        string schemaName)
    {
        var response = document
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty(method)
            .GetProperty("responses")
            .GetProperty(statusCode);
        var schema = response
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");

        Assert.Equal($"#/components/schemas/{schemaName}", schema.GetProperty("$ref").GetString());
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"opencodex-smoke-{Guid.NewGuid():N}");
            StaticPath = System.IO.Path.Combine(Path, "admin-static");
            DatabasePath = System.IO.Path.Combine(Path, "test.db");
            Directory.CreateDirectory(StaticPath);
        }

        public string Path { get; }

        public string StaticPath { get; }

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

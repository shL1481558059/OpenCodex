using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyRequestBodyLimitTests : IClassFixture<OpenCodexApiFactory>
{
    private readonly OpenCodexApiFactory _factory;

    public ProxyRequestBodyLimitTests(OpenCodexApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void ResolveMaxRequestBodyBytes_UsesConfigurationOverride()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENCODEX_MAX_REQUEST_BODY_MB"] = "1"
            })
            .Build();

        Assert.Equal(1024 * 1024, OpenCodex.Api.Infrastructure.ProxyRequestLimits
            .ResolveMaxRequestBodyBytes(configuration));
    }

    [Fact]
    public async Task OversizedBody_ReturnsCompatiblePayloadTooLarge()
    {
        using var client = CreateClient(maxRequestBodyMb: 1);
        var cookie = await LoginAsync(client);
        var apiKey = await CreateApiKeyAsync(client, cookie);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { model = "gpt-5", input = new string('x', 2 * 1024 * 1024) }),
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"message\":\"request body too large\",\"type\":\"proxy_error\"}}",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BodyWithinLimit_IsNotRejectedByTheLimit()
    {
        using var client = CreateClient(maxRequestBodyMb: 1);
        var cookie = await LoginAsync(client);
        var apiKey = await CreateApiKeyAsync(client, cookie);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent.Create(new { model = "gpt-5", input = "hello" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request);

        // 未配置渠道时业务错误为 400；关键是不被请求体上限拦截为 413。
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpClient CreateClient(int maxRequestBodyMb)
    {
        var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENCODEX_MAX_REQUEST_BODY_MB"] = maxRequestBodyMb.ToString()
            })));
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        using var response = await client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = OpenCodexApiFactory.AdminPassword
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        return cookies
            .Select(value => value.Split(';', 2)[0])
            .First(value => value.StartsWith("opencodex_admin_auth=", StringComparison.Ordinal));
    }

    private static async Task<string> CreateApiKeyAsync(HttpClient client, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api-keys")
        {
            Content = JsonContent.Create(new
            {
                owner_username = "admin",
                name = $"limit-test-{Guid.NewGuid():N}"
            })
        };
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("Data").GetProperty("key").GetProperty("key").GetString()!;
    }
}

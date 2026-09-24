using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyAuthenticationPipelineTests : IClassFixture<OpenCodexApiFactory>
{
    private const string MissingCredentialBody =
        "{\"error\":{\"message\":\"valid bearer api key required\",\"type\":\"bad_request\"}}";

    private const string MissingCredentialAdminBody =
        "{\"ErrorCode\":401,\"ErrorMsg\":\"valid bearer api key required\",\"succeeded\":false}";

    private readonly OpenCodexApiFactory _factory;

    public ProxyAuthenticationPipelineTests(OpenCodexApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task MissingCredential_ReturnsProtocolSpecificUnauthorizedBody()
    {
        using var client = CreateClient();

        using var compatible = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, compatible.StatusCode);
        Assert.Equal(MissingCredentialBody, await compatible.Content.ReadAsStringAsync());

        using var admin = await client.GetAsync("/models");
        Assert.Equal(HttpStatusCode.Unauthorized, admin.StatusCode);
        Assert.Equal(MissingCredentialAdminBody, await admin.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Basic abc")]
    [InlineData("Bearer")]
    [InlineData("BearerTOKEN")]
    [InlineData("Bearer    ")]
    public async Task MalformedCredential_ReturnsUnauthorized(string header)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", header);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(MissingCredentialBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownCredential_ReturnsUnauthorized()
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "ocx_unknown_key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(MissingCredentialBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DisabledCredential_ReturnsUnauthorized()
    {
        using var client = CreateClient();
        var cookie = await LoginAsync(client);
        var created = await CreateApiKeyAsync(client, cookie, "pipeline-disabled");

        var disabled = await SendJsonWithCookie(
            client,
            HttpMethod.Patch,
            $"/api-keys/{created.KeyId}",
            cookie,
            new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.Key);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(MissingCredentialBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AdminCookie_DoesNotAuthenticateProxyEndpoint()
    {
        using var client = CreateClient();
        var cookie = await LoginAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(MissingCredentialBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidCredential_AuthenticatesVersionedAndAdminModelRoutes()
    {
        using var client = CreateClient();
        var cookie = await LoginAsync(client);
        var created = await CreateApiKeyAsync(client, cookie, "pipeline-valid");

        using var compatibleRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        compatibleRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.Key);
        using var compatible = await client.SendAsync(compatibleRequest);

        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/models");
        adminRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.Key);
        using var admin = await client.SendAsync(adminRequest);

        Assert.Equal(HttpStatusCode.OK, compatible.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task CredentialWithExtraWhitespace_IsAcceptedAfterTrim()
    {
        using var client = CreateClient();
        var cookie = await LoginAsync(client);
        var created = await CreateApiKeyAsync(client, cookie, "pipeline-whitespace");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer   {created.Key}  ");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MissingCredential_RejectsRequestBeforeEndpointExecutes()
    {
        using var client = CreateClient();

        // 图片端点没有注册 IProxyImagesEndpointService，若动作执行会以 500 暴露；
        // 返回 401 说明请求在动作执行前被拒。
        using var response = await client.PostAsync("/v1/images/edits", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(MissingCredentialBody, await response.Content.ReadAsStringAsync());
    }

    private HttpClient CreateClient()
    {
        return _factory.CreateClient(new WebApplicationFactoryClientOptions
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

        var cookie = cookies
            .Select(value => value.Split(';', 2)[0])
            .FirstOrDefault(value => value.StartsWith("opencodex_admin_auth=", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(cookie));
        return cookie!;
    }

    private static async Task<(Guid KeyId, string Key)> CreateApiKeyAsync(
        HttpClient client,
        string cookie,
        string name)
    {
        using var response = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/api-keys",
            cookie,
            new { owner_username = "admin", name = $"{name}-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var data = document.RootElement.GetProperty("Data");
        var key = data.GetProperty("key");
        return (
            key.GetProperty("id").GetGuid(),
            key.GetProperty("key").GetString()!);
    }

    private static async Task<HttpResponseMessage> SendJsonWithCookie(
        HttpClient client,
        HttpMethod method,
        string path,
        string cookie,
        object payload)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }
}

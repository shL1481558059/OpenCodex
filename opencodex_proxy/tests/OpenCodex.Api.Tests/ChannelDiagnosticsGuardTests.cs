using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ChannelDiagnosticsGuardTests : IDisposable
{
    private const string AdminPassword = "test-password";
    private readonly ChannelDiagnosticsGuardFactory _factory = new();
    private readonly HttpClient _client;

    public ChannelDiagnosticsGuardTests()
    {
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
    }

    [Theory]
    [InlineData("${OPENCODEX_SECRET_KEY}")]
    [InlineData("$DB_PASSWORD")]
    [InlineData("https://example.com/v1?x=${SECRET}")]
    public async Task DiscoverModels_RejectsEnvironmentPlaceholders(string apikey)
    {
        var cookie = await LoginAndReadSessionCookie();
        var response = await SendJsonWithCookie(
            HttpMethod.Post,
            "/discover-models",
            cookie,
            new
            {
                id = Guid.NewGuid().ToString(),
                name = "Guard Channel",
                type = ProtocolConverter.Responses,
                baseurl = "https://upstream.example/v1",
                apikey,
                auth_mode = "config",
                capacity = 3,
                models = new[]
                {
                    new { model = "public-model", upstream_model = "public-model" }
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("environment variable placeholders", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8000/v1")]
    [InlineData("http://localhost:8080/v1")]
    [InlineData("http://10.0.0.1/v1")]
    [InlineData("http://172.16.0.1/v1")]
    [InlineData("http://192.168.1.1/v1")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    public async Task DiscoverModels_RejectsNonPublicBaseUrl(string baseurl)
    {
        var cookie = await LoginAndReadSessionCookie();
        var response = await SendJsonWithCookie(
            HttpMethod.Post,
            "/discover-models",
            cookie,
            new
            {
                id = Guid.NewGuid().ToString(),
                name = "Guard Channel",
                type = ProtocolConverter.Responses,
                baseurl,
                apikey = "secret",
                auth_mode = "config",
                capacity = 3,
                models = new[]
                {
                    new { model = "public-model", upstream_model = "public-model" }
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("non-public baseurl host", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverModels_AllowsPublicDomainWithoutDns()
    {
        var cookie = await LoginAndReadSessionCookie();
        var response = await SendJsonWithCookie(
            HttpMethod.Post,
            "/discover-models",
            cookie,
            new
            {
                id = Guid.NewGuid().ToString(),
                name = "Guard Channel",
                type = ProtocolConverter.Responses,
                baseurl = "https://upstream.example/v1",
                apikey = "secret",
                auth_mode = "config",
                capacity = 3,
                models = new[]
                {
                    new { model = "public-model", upstream_model = "public-model" }
                }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TestChannelStream_ClampsTimeoutAndForcesRetryZero()
    {
        var cookie = await LoginAndReadSessionCookie();
        var channelId = await CreateChannelAsync(
            cookie,
            timeoutSeconds: 999,
            retryCount: 5);

        var (statusCode, body) = await SendStreamRequestWithCookie(
            "/test-channel/stream",
            cookie,
            new
            {
                channel_id = channelId,
                model = "public-model",
                input = "你好",
                max_output_tokens = 32
            });

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Contains("pong", body, StringComparison.Ordinal);

        var (channel, payload) = Assert.Single(_factory.CapturedStreamChannels);
        Assert.Equal(60, channel["timeout_seconds"]);
        Assert.Equal(0, channel["retry_count"]);
        Assert.NotNull(payload);
    }

    [Fact]
    public async Task TestChannelStream_ClampsOutputTokensAndTruncatesInput()
    {
        var cookie = await LoginAndReadSessionCookie();
        var channelId = await CreateChannelAsync(cookie, type: ProtocolConverter.Chat);

        var longInput = new string('x', 5000);
        var (statusCode, body) = await SendStreamRequestWithCookie(
            "/test-channel/stream",
            cookie,
            new
            {
                channel_id = channelId,
                model = "public-model",
                input = longInput,
                max_output_tokens = 5000
            });

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Contains("pong", body, StringComparison.Ordinal);

        var (_, payload) = Assert.Single(_factory.CapturedStreamChannels);
        var captured = Assert.IsType<System.Collections.Generic.Dictionary<string, object?>>(payload);
        Assert.Equal(1024, captured["max_tokens"]);
        var messages = Assert.IsType<System.Collections.Generic.List<object?>>(captured["messages"]);
        var message = Assert.IsType<System.Collections.Generic.Dictionary<string, object?>>(messages[0]);
        var content = Assert.IsType<string>(message["content"]);
        Assert.Equal(4000, content.Length);
    }

    [Fact]
    public async Task TestChannelStream_NormalUserCannotTestOthersChannel()
    {
        var adminCookie = await LoginAndReadSessionCookie();
        var channelId = await CreateChannelAsync(adminCookie);

        var username = $"guard-user-{Guid.NewGuid():N}";
        var userCookie = await CreateUserAndLogin(adminCookie, username);

        var (statusCode, body) = await SendStreamRequestWithCookie(
            "/test-channel/stream",
            userCookie,
            new
            {
                channel_id = channelId,
                model = "public-model",
                input = "你好",
                max_output_tokens = 32
            });

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Contains("404", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestChannelStream_SuperadminCanTestOthersChannel()
    {
        var adminCookie = await LoginAndReadSessionCookie();
        var channelId = await CreateChannelAsync(adminCookie);

        var (statusCode, body) = await SendStreamRequestWithCookie(
            "/test-channel/stream",
            adminCookie,
            new
            {
                channel_id = channelId,
                model = "public-model",
                input = "你好",
                max_output_tokens = 32
            });

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Contains("pong", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestChannelStream_DisabledChannelStillTestable()
    {
        var cookie = await LoginAndReadSessionCookie();
        var channelId = await CreateChannelAsync(cookie, enabled: false);

        var (statusCode, body) = await SendStreamRequestWithCookie(
            "/test-channel/stream",
            cookie,
            new
            {
                channel_id = channelId,
                model = "public-model",
                input = "你好",
                max_output_tokens = 32
            });

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Contains("pong", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestChannelStream_ChannelIdNotFoundReturns404()
    {
        var cookie = await LoginAndReadSessionCookie();

        var (statusCode, body) = await SendStreamRequestWithCookie(
            "/test-channel/stream",
            cookie,
            new
            {
                channel_id = Guid.NewGuid(),
                model = "public-model",
                input = "你好",
                max_output_tokens = 32
            });

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Contains("404", body, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<string> LoginAndReadSessionCookie()
    {
        var response = await _client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = AdminPassword
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        var cookie = cookies
            .Select(value => value.Split(';', 2)[0])
            .FirstOrDefault(value => value.StartsWith("opencodex_admin_auth=", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(cookie));
        return cookie;
    }

    private async Task<string> CreateUserAndLogin(string adminCookie, string username)
    {
        var created = await SendJsonWithCookie(
            HttpMethod.Post,
            "/users",
            adminCookie,
            new
            {
                username,
                password = "user-password",
                enabled = true
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var login = await _client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = "user-password"
            }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Headers.TryGetValues("Set-Cookie", out var cookies));
        var cookie = cookies
            .Select(value => value.Split(';', 2)[0])
            .FirstOrDefault(value => value.StartsWith("opencodex_admin_auth=", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(cookie));
        return cookie;
    }

    private async Task<Guid> CreateChannelAsync(
        string cookie,
        int? timeoutSeconds = null,
        int? retryCount = null,
        bool? enabled = null,
        string? type = null)
    {
        var response = await SendJsonWithCookie(
            HttpMethod.Post,
            "/channels",
            cookie,
            new
            {
                id = Guid.NewGuid().ToString(),
                name = $"Guard Channel {Guid.NewGuid():N}",
                type = type ?? ProtocolConverter.Responses,
                baseurl = "https://upstream.example/v1",
                apikey = "secret",
                auth_mode = "config",
                timeout_seconds = timeoutSeconds ?? 30,
                retry_count = retryCount ?? 0,
                capacity = 3,
                enabled = enabled ?? true,
                models = new[]
                {
                    new
                    {
                        model = "public-model",
                        upstream_model = type == ProtocolConverter.Chat
                            ? "gpt-4o-2024-08-06"
                            : "public-model"
                    }
                }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var channel = document.RootElement.GetProperty("Data");
        return channel.GetProperty("id").GetGuid();
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendStreamRequestWithCookie(
        string requestUri,
        string cookie,
        object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Cookie", cookie);
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var responseBody = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, responseBody);
    }

    private Task<HttpResponseMessage> SendJsonWithCookie(
        HttpMethod method,
        string requestUri,
        string cookie,
        object? body)
    {
        var request = new HttpRequestMessage(method, requestUri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Add("Cookie", cookie);
        return _client.SendAsync(request);
    }

    private sealed class ChannelDiagnosticsGuardFactory : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "opencodex-channel-diagnostics-guard-tests",
            $"{Guid.NewGuid():N}.db");

        public List<(Dictionary<string, object?> Channel, Dictionary<string, object?>? Payload)> CapturedStreamChannels { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OPENCODEX_ADMIN_USERNAME"] = "admin",
                    ["OPENCODEX_ADMIN_PASSWORD"] = AdminPassword,
                    ["OPENCODEX_DB_PROVIDER"] = "sqlite",
                    ["OPENCODEX_DB_CONNECTION_STRING"] = $"Data Source={DbPath}",
                    ["OPENCODEX_DEFAULT_TIMEOUT"] = "120"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUpstreamClient>();
                services.RemoveAll<IUpstreamModelClient>();
                services.AddSingleton<IUpstreamClient>(new CapturingUpstreamClient(CapturedStreamChannels));
                services.AddSingleton<IUpstreamModelClient>(new SuccessModelClient());
            });
        }
    }

    private sealed class CapturingUpstreamClient : IUpstreamClient
    {
        private readonly List<(Dictionary<string, object?> Channel, Dictionary<string, object?>? Payload)> _captured;

        public CapturingUpstreamClient(
            List<(Dictionary<string, object?> Channel, Dictionary<string, object?>? Payload)> captured)
        {
            _captured = captured;
        }

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            _captured.Add((
                channel.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                payload.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
            return Task.FromResult(new Dictionary<string, object?>());
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            _captured.Add((
                channel.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                payload.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
            if (string.Equals(
                    JsonDictionaryValue.String(channel, "type"),
                    ProtocolConverter.Chat,
                    StringComparison.Ordinal))
            {
                yield return "data: {\"id\":\"chatcmpl_test\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o-2024-08-06\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"pong\"},\"finish_reason\":null}]}";
                yield return "";
                yield return "data: {\"id\":\"chatcmpl_test\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o-2024-08-06\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1}}";
                yield return "";
                yield return "data: [DONE]";
                yield return "";
            }
            else
            {
                yield return "event: response.output_text.delta\n";
                yield return "data: {\"type\":\"response.output_text.delta\",\"delta\":\"pong\"}\n";
                yield return "\n";
                yield return "event: response.completed\n";
                yield return "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_test\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"public-model\",\"output\":[{\"id\":\"msg_test\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"pong\",\"annotations\":[]}]}]}}\n";
                yield return "\n";
            }
        }
    }

    private sealed class SuccessModelClient : IUpstreamModelClient
    {
        public Task<Dictionary<string, object?>> ListModelsAsync(
            IReadOnlyDictionary<string, object?> channel,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new Dictionary<string, object?>
            {
                ["data"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = "public-model"
                    }
                }
            });
        }
    }
}

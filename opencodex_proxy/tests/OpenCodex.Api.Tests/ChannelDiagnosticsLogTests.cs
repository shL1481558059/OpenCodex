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
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ChannelDiagnosticsLogTests : IDisposable
{
    private const string AdminPassword = "test-password";
    private const string SecretApiKey = "diag-secret-key";
    private const string SecretHeaderValue = "header-secret-value";
    private readonly ChannelDiagnosticsApiFactory _factory = new();
    private readonly HttpClient _client;

    public ChannelDiagnosticsLogTests()
    {
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
    }

    [Fact]
    public async Task TestChannelWritesRequestLogWithoutSecrets()
    {
        var cookie = await LoginAndReadSessionCookie();

        var response = await SendJsonWithCookie(
            HttpMethod.Post,
            "/test-channel",
            cookie,
            new
            {
                id = "diag-channel",
                name = "Diagnostics Channel",
                type = ProtocolConverter.Responses,
                baseurl = "https://upstream.example/v1",
                apikey = SecretApiKey,
                auth_mode = "config",
                headers = new Dictionary<string, object?>
                {
                    ["Authorization"] = $"Bearer {SecretHeaderValue}",
                    ["x-api-key"] = SecretHeaderValue,
                    ["X-Normal"] = "visible"
                },
                models = new[]
                {
                    new
                    {
                        model = "public-model",
                        upstream_model = "upstream-model",
                        supports_image = false
                    }
                },
                model = "public-model",
                input = "你好",
                max_output_tokens = 32
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = OpenCodexDbContextFactory.Create(_factory.DbPath);
        var log = Assert.Single(context.RequestLogs.Where(item => item.Path == "/test-channel"));
        Assert.Equal("POST", log.Method);
        Assert.Equal("public-model", log.Model);
        Assert.Equal("upstream-model", log.UpstreamModel);
        Assert.Equal("diag-channel", log.ChannelId);
        Assert.Equal("admin", log.OwnerUsername);
        Assert.Null(log.ApiKeyId);
        Assert.True(log.IsStream);
        Assert.Equal(200, log.StatusCode);

        var detail = context.RequestLogDetails.Single(item => item.RequestLogId == log.Id);
        Assert.NotNull(detail);
        var persistedDetail = string.Concat(
            detail.RequestHeaders,
            detail.RequestBody,
            detail.UpstreamRequestBody,
            detail.UpstreamResponseBody,
            detail.ResponseBody);
        Assert.DoesNotContain(SecretApiKey, persistedDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretHeaderValue, persistedDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("opencodex_admin_auth", persistedDetail, StringComparison.Ordinal);
        Assert.Contains("\"X-Normal\":\"visible\"", detail.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"stream\":true", detail.UpstreamRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"你好\"", detail.UpstreamRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"x-oai-attestation\":\"test-attestation\"", detail.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"originator\":\"Codex Desktop\"", detail.RequestBody, StringComparison.Ordinal);
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

    private Task<HttpResponseMessage> SendJsonWithCookie(
        HttpMethod method,
        string requestUri,
        string cookie,
        object body)
    {
        var request = new HttpRequestMessage(method, requestUri)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Cookie", cookie);
        return _client.SendAsync(request);
    }

    private sealed class ChannelDiagnosticsApiFactory : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "opencodex-channel-diagnostics-tests",
            $"{Guid.NewGuid():N}.db");

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
                    ["OPENCODEX_DB_PATH"] = DbPath,
                    ["OPENCODEX_DEFAULT_TIMEOUT"] = "120"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUpstreamClient>();
                services.AddSingleton<IUpstreamClient, SuccessfulUpstreamClient>();
            });
        }
    }

    private sealed class SuccessfulUpstreamClient : IUpstreamClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new();

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new Dictionary<string, object?>
            {
                ["id"] = "resp_test",
                ["model"] = JsonElementValue(payload, "model"),
                ["output"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "output_text",
                                ["text"] = "pong"
                            }
                        }
                    }
                },
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = 3,
                    ["output_tokens"] = 5
                }
            });
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            var response = new Dictionary<string, object?>
            {
                ["id"] = "resp_test",
                ["model"] = JsonElementValue(payload, "model"),
                ["output"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "output_text",
                                ["text"] = "pong"
                            }
                        }
                    }
                },
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = 3,
                    ["output_tokens"] = 5
                }
            };
            yield return "event: response.output_text.delta\n";
            yield return "data: {\"type\":\"response.output_text.delta\",\"delta\":\"pong\"}\n";
            yield return "\n";
            yield return "event: response.completed\n";
            yield return $"data: {{\"type\":\"response.completed\",\"response\":{JsonSerializer.Serialize(response, JsonOptions)}}}\n";
            yield return "\n";
        }

        private static string JsonElementValue(
            IReadOnlyDictionary<string, object?> payload,
            string key)
        {
            return payload.TryGetValue(key, out var value)
                ? Convert.ToString(value) ?? string.Empty
                : string.Empty;
        }
    }
}

using System.Net;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCodex.Core.Errors;
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
    public async Task TestChannelStreamWritesRequestLogWithoutSecrets()
    {
        var cookie = await LoginAndReadSessionCookie();

        var (statusCode, body) = await SendStreamRequestWithCookie(
            "/test-channel/stream",
            cookie,
            new
            {
                id = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                name = "Diagnostics Channel",
                type = ProtocolConverter.Responses,
                baseurl = "https://upstream.example/v1",
                apikey = SecretApiKey,
                auth_mode = "config",
                capacity = 3,
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

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Contains("pong", body, StringComparison.Ordinal);
        Assert.Contains("response.completed", body, StringComparison.Ordinal);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={_factory.DbPath}");
        context.Database.Migrate();
var log = Assert.Single(context.RequestLogs.Where(item => item.Path == "/test-channel/stream"));
        Assert.Equal("POST", log.Method);
        Assert.Equal("public-model", log.Model);
        Assert.Equal("upstream-model", log.UpstreamModel);
        Assert.NotNull(log.ChannelId);
        Assert.NotEqual(Guid.Empty, log.OwnerUserId);
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
    }

    [Fact]
    public async Task TestChannelStreamForChatChannelExtractsOutputText()
    {
        _factory.UpstreamClient = new ChatUpstreamClient();
        var cookie = await LoginAndReadSessionCookie();

        var (statusCode, body) = await SendStreamRequestWithCookie(
            "/test-channel/stream",
            cookie,
            new
            {
                id = "chat-channel",
                name = "Chat Channel",
                type = ProtocolConverter.Chat,
                baseurl = "https://upstream.example/v1",
                apikey = SecretApiKey,
                auth_mode = "config",
                capacity = 3,
                models = new[]
                {
                    new
                    {
                        model = "gpt-4o",
                        upstream_model = "gpt-4o-2024-08-06",
                        supports_image = false
                    }
                },
                model = "gpt-4o",
                input = "你好",
                max_output_tokens = 32
            });

        Assert.Equal(HttpStatusCode.OK, statusCode);
        // SseStreamConverter.ChatToResponsesEvents 会将 chat chunk 的 content 转为
        // response.output_text.delta 事件，ChannelTestStreamCapture 应能提取 output_text。
        Assert.Contains("response.output_text.delta", body, StringComparison.Ordinal);
        Assert.Contains("pong", body, StringComparison.Ordinal);
        Assert.Contains("response.completed", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestChannelStreamForConfigErrorEmitsErrorEvent()
    {
        var cookie = await LoginAndReadSessionCookie();

        var (statusCode, body) = await SendStreamRequestWithCookie(
            "/test-channel/stream",
            cookie,
            new { });

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Contains("channel_test.error", body, StringComparison.Ordinal);
        Assert.Contains("config_error", body, StringComparison.Ordinal);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={_factory.DbPath}");
        context.Database.Migrate();
var log = Assert.Single(context.RequestLogs.Where(item => item.Path == "/test-channel/stream"));
        Assert.Equal(400, log.StatusCode);
        Assert.False(string.IsNullOrEmpty(log.Error));
    }

    [Fact]
    public async Task TestChannelStreamForUpstreamErrorEmitsErrorEvent()
    {
        _factory.UpstreamClient = new FailingUpstreamClient(
            new UpstreamException("upstream returned 429", 429));
        var cookie = await LoginAndReadSessionCookie();

        var (statusCode, body) = await SendStreamRequestWithCookie(
            "/test-channel/stream",
            cookie,
            new
            {
                id = "fail-channel",
                type = ProtocolConverter.Responses,
                baseurl = "https://upstream.example/v1",
                apikey = SecretApiKey,
                auth_mode = "config",
                capacity = 3,
                models = new[]
                {
                    new { model = "m", upstream_model = "m", supports_image = false }
                },
                model = "m",
                input = "你好",
                max_output_tokens = 32
            });

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Contains("channel_test.error", body, StringComparison.Ordinal);
        Assert.Contains("upstream_error", body, StringComparison.Ordinal);
        Assert.Contains("429", body, StringComparison.Ordinal);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={_factory.DbPath}");
        context.Database.Migrate();
var log = Assert.Single(context.RequestLogs.Where(item => item.Path == "/test-channel/stream"));
        Assert.Equal(429, log.StatusCode);
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

    private sealed class ChannelDiagnosticsApiFactory : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "opencodex-channel-diagnostics-tests",
            $"{Guid.NewGuid():N}.db");

        public IUpstreamClient UpstreamClient { get; set; } = new ResponsesUpstreamClient();

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
                services.AddSingleton(_ => UpstreamClient);
            });
        }
    }

    /// <summary>
    /// 上游返回 Responses 协议流式事件。
    /// </summary>
    private sealed class ResponsesUpstreamClient : IUpstreamClient
    {
        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new Dictionary<string, object?>());
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return "event: response.output_text.delta\n";
            yield return "data: {\"type\":\"response.output_text.delta\",\"delta\":\"pong\"}\n";
            yield return "\n";
            yield return "event: response.completed\n";
            yield return "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_test\",\"model\":\"upstream-model\",\"output\":[],\"usage\":{\"input_tokens\":3,\"output_tokens\":5}}}\n";
            yield return "\n";
        }
    }

    /// <summary>
    /// 上游返回 Chat 协议流式事件（chat.completion.chunk）。
    /// </summary>
    private sealed class ChatUpstreamClient : IUpstreamClient
    {
        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new Dictionary<string, object?>());
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            var chunk = new Dictionary<string, object?>
            {
                ["id"] = "chatcmpl_test",
                ["object"] = "chat.completion.chunk",
                ["model"] = "gpt-4o-2024-08-06",
                ["choices"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["delta"] = new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = "pong"
                        },
                        ["finish_reason"] = null
                    }
                }
            };
            yield return $"data: {JsonSerializer.Serialize(chunk)}";
            yield return "";
            var doneChunk = new Dictionary<string, object?>
            {
                ["id"] = "chatcmpl_test",
                ["object"] = "chat.completion.chunk",
                ["model"] = "gpt-4o-2024-08-06",
                ["choices"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["delta"] = new Dictionary<string, object?>(),
                        ["finish_reason"] = "stop"
                    }
                },
                ["usage"] = new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 3,
                    ["completion_tokens"] = 1
                }
            };
            yield return $"data: {JsonSerializer.Serialize(doneChunk)}";
            yield return "";
            yield return "data: [DONE]";
            yield return "";
        }
    }

    /// <summary>
    /// 上游抛出异常，用于测试错误路径。
    /// </summary>
    private sealed class FailingUpstreamClient : IUpstreamClient
    {
        private readonly UpstreamException _exception;

        public FailingUpstreamClient(UpstreamException exception)
        {
            _exception = exception;
        }

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            throw _exception;
        }

        public IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            return ThrowStream(_exception);
        }

        private static async IAsyncEnumerable<string> ThrowStream(
            UpstreamException exception,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
#pragma warning disable CS0162
            await Task.CompletedTask;
            throw exception;
            yield break; // unreachable, satisfies async-iterator requirement
#pragma warning restore CS0162
        }
    }
}

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
using OpenCodex.Core.Domain;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
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
    public async Task TestChannelStreamWritesCompleteRequestLogContent()
    {
        var cookie = await LoginAndReadSessionCookie();
        var channelId = await CreateChannelAsync(cookie);

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
        Assert.Equal(ProxyRequestTypes.Diagnostic, log.RequestType);

        var detail = new LogContentStore(context).Read(log.Id);
        var persistedDetail = string.Concat(
            detail.Get(RequestLogContentSlot.RequestHeaders),
            detail.Get(RequestLogContentSlot.RequestBody),
            detail.Get(RequestLogContentSlot.UpstreamRequestBody),
            detail.Get(RequestLogContentSlot.UpstreamResponseBody),
            detail.Get(RequestLogContentSlot.ResponseBody));
        Assert.Contains(SecretApiKey, persistedDetail, StringComparison.Ordinal);
        Assert.Contains(SecretHeaderValue, persistedDetail, StringComparison.Ordinal);
        Assert.Contains("opencodex_admin_auth", persistedDetail, StringComparison.Ordinal);
        Assert.Contains("\"X-Normal\":\"visible\"", detail.Get(RequestLogContentSlot.RequestBody), StringComparison.Ordinal);
        Assert.Contains("\"stream\":true", detail.Get(RequestLogContentSlot.UpstreamRequestBody), StringComparison.Ordinal);
        Assert.Contains("\"text\":\"你好\"", detail.Get(RequestLogContentSlot.UpstreamRequestBody), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestChannelStreamEmitsDiagnosticDetailEvent()
    {
        var cookie = await LoginAndReadSessionCookie();
        var channelId = await CreateChannelAsync(cookie);

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
        Assert.Contains("event: channel_test.completed", body, StringComparison.Ordinal);
        Assert.True(
            body.IndexOf("event: channel_test.completed", StringComparison.Ordinal)
            < body.IndexOf("data: [DONE]", StringComparison.Ordinal));
        Assert.Contains("\"status_code\":200", body, StringComparison.Ordinal);
        Assert.Contains("\"request_model\":\"public-model\"", body, StringComparison.Ordinal);
        Assert.Contains("\"upstream_model\":\"upstream-model\"", body, StringComparison.Ordinal);
        Assert.Contains("\"upstream_response\"", body, StringComparison.Ordinal);
        Assert.Contains("\"output\":[{\"id\":\"msg_test\"", body, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"pong\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"output_text\":\"pong\"", body, StringComparison.Ordinal);
        Assert.Contains("\"upstream_request\"", body, StringComparison.Ordinal);
        Assert.Contains("\"stream\":true", body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretApiKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretHeaderValue, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestChannelStreamForChatChannelExtractsOutputText()
    {
        _factory.UpstreamClient = new ChatUpstreamClient();
        var cookie = await LoginAndReadSessionCookie();
        var channelId = await CreateChannelAsync(cookie, type: ProtocolConverter.Chat);

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
        Assert.Contains("response.output_text.delta", body, StringComparison.Ordinal);
        Assert.Contains("pong", body, StringComparison.Ordinal);
        Assert.Contains("response.completed", body, StringComparison.Ordinal);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={_factory.DbPath}");
        context.Database.Migrate();
        var log = Assert.Single(context.RequestLogs.Where(item => item.Path == "/test-channel/stream"));
        var detail = new LogContentStore(context).Read(log.Id);
        var upstreamResponse = detail.Get(RequestLogContentSlot.UpstreamResponseBody);
        Assert.Contains("\"object\":\"chat.completion\"", upstreamResponse, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"pong\"", upstreamResponse, StringComparison.Ordinal);
        Assert.Contains("\"finish_reason\":\"stop\"", upstreamResponse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestChannelStreamForMessagesChannelCapturesOriginalResponse()
    {
        _factory.UpstreamClient = new MessagesUpstreamClient();
        var cookie = await LoginAndReadSessionCookie();
        var channelId = await CreateChannelAsync(cookie, type: ProtocolConverter.Messages);

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
        Assert.Contains("response.output_text.delta", body, StringComparison.Ordinal);
        Assert.Contains("pong", body, StringComparison.Ordinal);
        Assert.Contains("response.completed", body, StringComparison.Ordinal);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={_factory.DbPath}");
        context.Database.Migrate();
        var log = Assert.Single(context.RequestLogs.Where(item => item.Path == "/test-channel/stream"));
        var detail = new LogContentStore(context).Read(log.Id);
        var upstreamResponse = detail.Get(RequestLogContentSlot.UpstreamResponseBody);
        Assert.Contains("\"type\":\"message\"", upstreamResponse, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"claude-sonnet-upstream\"", upstreamResponse, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"pong\"", upstreamResponse, StringComparison.Ordinal);
        Assert.Contains("\"stop_reason\":\"end_turn\"", upstreamResponse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestChannelStreamForMissingChannelEmitsErrorEvent()
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
        Assert.Contains("channel_test.error", body, StringComparison.Ordinal);
        Assert.Contains("404", body, StringComparison.Ordinal);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={_factory.DbPath}");
        context.Database.Migrate();
        var log = Assert.Single(context.RequestLogs.Where(item => item.Path == "/test-channel/stream"));
        Assert.Equal(404, log.StatusCode);
        Assert.Equal(ProxyRequestTypes.Diagnostic, log.RequestType);
    }

    [Fact]
    public async Task TestChannelStreamForUpstreamErrorEmitsErrorEvent()
    {
        _factory.UpstreamClient = new FailingUpstreamClient(
            new UpstreamException("upstream returned 429", 429));
        var cookie = await LoginAndReadSessionCookie();
        var channelId = await CreateChannelAsync(cookie);

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
        Assert.Contains("channel_test.error", body, StringComparison.Ordinal);
        Assert.Contains("upstream_error", body, StringComparison.Ordinal);
        Assert.Contains("429", body, StringComparison.Ordinal);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={_factory.DbPath}");
        context.Database.Migrate();
        var log = Assert.Single(context.RequestLogs.Where(item => item.Path == "/test-channel/stream"));
        Assert.Equal(429, log.StatusCode);
        Assert.Equal(ProxyRequestTypes.Diagnostic, log.RequestType);
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

    private async Task<Guid> CreateChannelAsync(string cookie, string? type = null)
    {
        var response = await SendJsonWithCookie(
            HttpMethod.Post,
            "/channels",
            cookie,
            new
            {
                id = Guid.NewGuid().ToString(),
                name = "Diagnostics Channel",
                type = type ?? ProtocolConverter.Responses,
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
                        upstream_model = type == ProtocolConverter.Chat
                            ? "gpt-4o-2024-08-06"
                            : type == ProtocolConverter.Messages
                                ? "claude-sonnet-upstream"
                                : "upstream-model",
                        supports_image = false
                    }
                }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadChannelIdFromChannelsResponse(cookie, "Diagnostics Channel");
    }

    private async Task<Guid> ReadChannelIdFromChannelsResponse(string cookie, string channelName)
    {
        var config = await SendJsonWithCookie(HttpMethod.Get, "/channels", cookie, null);
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        using var document = await JsonDocument.ParseAsync(await config.Content.ReadAsStreamAsync());
        foreach (var channel in document.RootElement.GetProperty("Data").GetProperty("channels").EnumerateArray())
        {
            if (channel.GetProperty("name").GetString() == channelName)
            {
                return channel.GetProperty("id").GetGuid();
            }
        }

        throw new InvalidOperationException($"channel {channelName} not found");
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
            yield return "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_test\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"upstream-model\",\"output\":[{\"id\":\"msg_test\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"pong\",\"annotations\":[]}]}],\"usage\":{\"input_tokens\":3,\"output_tokens\":5}}}\n";
            yield return "\n";
        }
    }

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

    private sealed class MessagesUpstreamClient : IUpstreamClient
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
            yield return "event: message_start\n";
            yield return "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_test\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-upstream\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n";
            yield return "\n";
            yield return "event: content_block_start\n";
            yield return "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n";
            yield return "\n";
            yield return "event: content_block_delta\n";
            yield return "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"pong\"}}\n";
            yield return "\n";
            yield return "event: content_block_stop\n";
            yield return "data: {\"type\":\"content_block_stop\",\"index\":0}\n";
            yield return "\n";
            yield return "event: message_delta\n";
            yield return "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":1}}\n";
            yield return "\n";
            yield return "event: message_stop\n";
            yield return "data: {\"type\":\"message_stop\"}\n";
            yield return "\n";
        }
    }

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

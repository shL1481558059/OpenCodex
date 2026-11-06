using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Proxy;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class ResponsesUpstreamTransportTests
{
    [Fact]
    public async Task ContextPoolSendsOnlyDeltaWithPreviousResponseId()
    {
        await using var server = new ResponsesWebSocketServer();
        await using var pool = new ResponsesWebSocketPool();
        var transport = new ResponsesUpstreamTransport(
            new CountingHttpClient(),
            pool,
            NullLogger<ResponsesUpstreamTransport>.Instance);
        var channel = Channel(server.BaseUrl, "context_pool");
        var firstRequest = Request("hello");

        await ConsumeAsync(transport.StreamAsync(Context(channel, firstRequest), default));
        var first = await server.NextRequestAsync();
        Assert.Equal("hello", Text(JsonDictionaryValue.List(first, "input")[0]!));
        Assert.False(first.ContainsKey("previous_response_id"));

        var secondRequest = Request("hello", "next");
        JsonDictionaryValue.List(secondRequest, "input").Insert(1, Assistant("answer"));
        await ConsumeAsync(transport.StreamAsync(Context(channel, secondRequest), default));
        var second = await server.NextRequestAsync();
        Assert.Equal("resp-1", JsonDictionaryValue.String(second, "previous_response_id"));
        var input = JsonDictionaryValue.List(second, "input");
        Assert.Single(input);
        Assert.Equal("next", Text(input[0]!));
    }

    [Fact]
    public async Task ContextPoolStoresSnapshotBeforeTerminalEventIsConsumed()
    {
        await using var server = new ResponsesWebSocketServer();
        await using var pool = new ResponsesWebSocketPool();
        var transport = new ResponsesUpstreamTransport(
            new CountingHttpClient(),
            pool,
            NullLogger<ResponsesUpstreamTransport>.Instance);
        var channel = Channel(server.BaseUrl, "context_pool");

        await using (var stream = transport.StreamAsync(Context(channel, Request("hello")), default)
            .GetAsyncEnumerator())
        {
            while (await stream.MoveNextAsync())
            {
                if (stream.Current.Contains("event: response.completed", StringComparison.Ordinal)) break;
            }
        }
        _ = await server.NextRequestAsync();

        var nextRequest = Request("hello", "next");
        JsonDictionaryValue.List(nextRequest, "input").Insert(1, Assistant("answer"));
        await ConsumeAsync(transport.StreamAsync(Context(channel, nextRequest), default));

        var second = await server.NextRequestAsync();
        Assert.Equal("resp-1", JsonDictionaryValue.String(second, "previous_response_id"));
        Assert.Single(JsonDictionaryValue.List(second, "input"));
    }

    [Fact]
    public async Task ContextPoolReplaysFullHistoryWhenUpstreamLostPreviousResponse()
    {
        await using var server = new ResponsesWebSocketServer(rejectPreviousResponse: true);
        await using var pool = new ResponsesWebSocketPool();
        var transport = new ResponsesUpstreamTransport(
            new CountingHttpClient(),
            pool,
            NullLogger<ResponsesUpstreamTransport>.Instance);
        var channel = Channel(server.BaseUrl, "context_pool");

        await ConsumeAsync(transport.StreamAsync(Context(channel, Request("hello")), default));
        _ = await server.NextRequestAsync();

        var nextRequest = Request("hello", "next");
        JsonDictionaryValue.List(nextRequest, "input").Insert(1, Assistant("answer"));
        await ConsumeAsync(transport.StreamAsync(Context(channel, nextRequest), default));

        var delta = await server.NextRequestAsync();
        Assert.Equal("resp-1", JsonDictionaryValue.String(delta, "previous_response_id"));
        var replay = await server.NextRequestAsync();
        Assert.False(replay.ContainsKey("previous_response_id"));
        Assert.Equal(3, JsonDictionaryValue.List(replay, "input").Count);
    }

    [Fact]
    public async Task PassthroughForwardsPreviousResponseAndDeltaWithoutExpansion()
    {
        await using var server = new ResponsesWebSocketServer();
        await using var pool = new ResponsesWebSocketPool();
        var transport = new ResponsesUpstreamTransport(
            new CountingHttpClient(),
            pool,
            NullLogger<ResponsesUpstreamTransport>.Instance);
        var channel = Channel(server.BaseUrl, "passthrough");
        var original = Request("tool result");
        original["previous_response_id"] = "resp-existing";
        var upstream = Request("history", "tool result");

        await ConsumeAsync(transport.StreamAsync(
            Context(channel, upstream, method: "GET", originalPayload: original),
            default));

        var forwarded = await server.NextRequestAsync();
        Assert.Equal("resp-existing", JsonDictionaryValue.String(forwarded, "previous_response_id"));
        Assert.Single(JsonDictionaryValue.List(forwarded, "input"));
        Assert.Equal("tool result", Text(JsonDictionaryValue.List(forwarded, "input")[0]!));
    }

    [Fact]
    public async Task OffUsesHttpTransportWithoutOpeningWebSocket()
    {
        await using var server = new ResponsesWebSocketServer();
        await using var pool = new ResponsesWebSocketPool();
        var http = new CountingHttpClient();
        var transport = new ResponsesUpstreamTransport(
            http,
            pool,
            NullLogger<ResponsesUpstreamTransport>.Instance);
        var channel = Channel(server.BaseUrl, "off");

        await ConsumeAsync(transport.StreamAsync(Context(channel, Request("hello")), default));

        Assert.Equal(1, http.StreamCalls);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AutoCachesUnsupportedProbeAndFallsBackToHttp()
    {
        await using var pool = new ResponsesWebSocketPool();
        var http = new CountingHttpClient();
        var transport = new ResponsesUpstreamTransport(
            http,
            pool,
            NullLogger<ResponsesUpstreamTransport>.Instance);
        var channel = Channel("http://127.0.0.1:1", "auto");

        await ConsumeAsync(transport.StreamAsync(Context(channel, Request("first")), default));
        await ConsumeAsync(transport.StreamAsync(Context(channel, Request("second")), default));

        Assert.Equal(2, http.StreamCalls);
    }

    [Fact]
    public async Task ContextPoolDoesNotSilentlyFallbackToHttp()
    {
        await using var pool = new ResponsesWebSocketPool();
        var http = new CountingHttpClient();
        var transport = new ResponsesUpstreamTransport(
            http,
            pool,
            NullLogger<ResponsesUpstreamTransport>.Instance);
        var channel = Channel("http://127.0.0.1:1", "context_pool");

        await Assert.ThrowsAsync<UpstreamException>(() =>
            ConsumeAsync(transport.StreamAsync(Context(channel, Request("hello")), default)));

        Assert.Equal(0, http.StreamCalls);
    }

    [Fact]
    public async Task ContextPoolIsolatesClientKeysAndChannels()
    {
        await using var server = new ResponsesWebSocketServer();
        await using var pool = new ResponsesWebSocketPool();
        var transport = new ResponsesUpstreamTransport(
            new CountingHttpClient(),
            pool,
            NullLogger<ResponsesUpstreamTransport>.Instance);
        var channelA = Channel(server.BaseUrl, "context_pool", "channel-a");
        var channelB = Channel(server.BaseUrl, "context_pool", "channel-b");
        var clientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var clientB = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await ConsumeAsync(transport.StreamAsync(
            Context(channelA, Request("hello"), apiKeyId: clientA, channelId: "channel-a"),
            default));
        Assert.False((await server.NextRequestAsync()).ContainsKey("previous_response_id"));

        await ConsumeAsync(transport.StreamAsync(
            Context(channelA, Request("other"), apiKeyId: clientB, channelId: "channel-a"),
            default));
        Assert.False((await server.NextRequestAsync()).ContainsKey("previous_response_id"));

        await ConsumeAsync(transport.StreamAsync(
            Context(channelB, Request("hello"), apiKeyId: clientA, channelId: "channel-b"),
            default));
        Assert.False((await server.NextRequestAsync()).ContainsKey("previous_response_id"));

        var continued = Request("hello", "next");
        JsonDictionaryValue.List(continued, "input").Insert(1, Assistant("answer"));
        await ConsumeAsync(transport.StreamAsync(
            Context(channelA, continued, apiKeyId: clientA, channelId: "channel-a"),
            default));
        var fourth = await server.NextRequestAsync();
        Assert.Equal("resp-1", JsonDictionaryValue.String(fourth, "previous_response_id"));
        Assert.Equal("next", Text(JsonDictionaryValue.List(fourth, "input").Single()!));
    }

    private static D Channel(string baseUrl, string mode, string id = "channel-1") => new(StringComparer.Ordinal)
    {
        ["id"] = id,
        ["name"] = "responses",
        ["type"] = "responses",
        ["baseurl"] = baseUrl + "/v1",
        ["apikey"] = "secret",
        ["auth_mode"] = "config",
        ["headers"] = new D(),
        ["timeout_seconds"] = 5,
        ["compat"] = new D { ["ws_mode"] = mode }
    };

    private static D Request(params string[] texts)
    {
        return new D(StringComparer.Ordinal)
        {
            ["model"] = "gpt-test",
            ["store"] = false,
            ["stream"] = true,
            ["input"] = texts.Select(text => (object?)new D
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = text
            }).ToList()
        };
    }

    private static D Assistant(string text) => new(StringComparer.Ordinal)
    {
        ["id"] = "msg-resp-1",
        ["type"] = "message",
        ["role"] = "assistant",
        ["status"] = "completed",
        ["content"] = new List<object?>
        {
            new D { ["type"] = "output_text", ["text"] = text }
        }
    };

    private static ProxyStreamContext Context(
        D channel,
        D payload,
        string method = "POST",
        D? originalPayload = null,
        Guid? apiKeyId = null,
        string? channelId = null)
    {
        var route = new ProxyRouteDto(channel, "gpt-test", "gpt-test", false, true);
        return new ProxyStreamContext(
            0,
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            "tester",
            apiKeyId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            WebSearchPayload.DeepCopyObject(originalPayload ?? payload),
            WebSearchPayload.DeepCopyObject(payload),
            WebSearchPayload.DeepCopyObject(payload),
            "responses",
            route,
            "responses",
            channelId ?? JsonDictionaryValue.String(channel, "id"),
            "admin",
            "gpt-test",
            "gpt-test",
            5,
            new ProxyRequestMetadata(
                method,
                "/v1/responses",
                "127.0.0.1",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["thread-id"] = "thread-1"
                }),
            new NoopWriter(),
            default);
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<string> lines)
    {
        await foreach (var _ in lines) { }
    }

    private static string Text(object value)
    {
        return value is IReadOnlyDictionary<string, object?> item
            ? JsonDictionaryValue.String(item, "content")
            : "";
    }

    private sealed class CountingHttpClient : IUpstreamClient
    {
        public int StreamCalls { get; private set; }

        public Task<D> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken) => Task.FromResult(new D());

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamCalls++;
            yield return "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"http\",\"status\":\"completed\",\"output\":[]}}\n\n";
            await Task.CompletedTask;
        }
    }

    private sealed class NoopWriter : IProxyStreamWriter
    {
        public void PrepareSse() { }

        public Task<StreamWriteMetrics> WriteLinesAsync(
            IAsyncEnumerable<string> lines,
            Func<string, bool> countsForTtft,
            Func<int> elapsedMilliseconds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StreamWriteMetrics());
    }

    private sealed class ResponsesWebSocketServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly Channel<D> _requests = System.Threading.Channels.Channel.CreateUnbounded<D>();
        private readonly List<D> _recorded = [];
        private int _responseIndex;

        public ResponsesWebSocketServer(bool rejectPreviousResponse = false)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            _app = builder.Build();
            _app.UseWebSockets();
            _app.Map("/v1/responses", context => HandleAsync(context, rejectPreviousResponse));
            _app.StartAsync().GetAwaiter().GetResult();
            BaseUrl = _app.Urls.Single();
        }

        public string BaseUrl { get; }
        public IReadOnlyCollection<D> Requests
        {
            get
            {
                lock (_recorded) return _recorded.ToList();
            }
        }

        public ValueTask<D> NextRequestAsync() => _requests.Reader.ReadAsync();

        private async Task HandleAsync(HttpContext context, bool rejectPreviousResponse)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);

                using var json = JsonDocument.Parse(message.ToArray());
                var request = (D)WebSearchPayload.FromJsonElement(json.RootElement)!;
                lock (_recorded) _recorded.Add(request);
                await _requests.Writer.WriteAsync(request, context.RequestAborted);
                if (rejectPreviousResponse && request.ContainsKey("previous_response_id"))
                {
                    await SendAsync(socket, new D
                    {
                        ["type"] = "error",
                        ["error"] = new D
                        {
                            ["type"] = "invalid_request_error",
                            ["code"] = "previous_response_not_found",
                            ["message"] = "The previous response is no longer available."
                        }
                    }, context.RequestAborted);
                    continue;
                }
                var id = "resp-" + Interlocked.Increment(ref _responseIndex);
                await SendAsync(socket, new D
                {
                    ["type"] = "response.created",
                    ["response"] = new D
                    {
                        ["id"] = id,
                        ["object"] = "response",
                        ["status"] = "in_progress",
                        ["model"] = "gpt-test",
                        ["output"] = new List<object?>()
                    }
                }, context.RequestAborted);
                await SendAsync(socket, new D
                {
                    ["type"] = "response.completed",
                    ["response"] = new D
                    {
                        ["id"] = id,
                        ["object"] = "response",
                        ["status"] = "completed",
                        ["model"] = "gpt-test",
                        ["output"] = new List<object?>
                        {
                            new D
                            {
                                ["id"] = "msg-" + id,
                                ["type"] = "message",
                                ["role"] = "assistant",
                                ["status"] = "completed",
                                ["content"] = new List<object?>
                                {
                                    new D { ["type"] = "output_text", ["text"] = "answer" }
                                }
                            }
                        }
                    }
                }, context.RequestAborted);
            }
        }

        private static Task SendAsync(WebSocket socket, D payload, CancellationToken cancellationToken) =>
            socket.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(payload),
                WebSocketMessageType.Text,
                true,
                cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

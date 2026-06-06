using System.Diagnostics;
using System.Text.Json;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Protocols;
using OpenCodex.Api.Routing;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Proxy;

public sealed class ProxyNonStreamServiceTests
{
    [Fact]
    public async Task SendAsyncPostsUpstreamConvertsResponseAndLogsSuccess()
    {
        var upstream = new FakeUpstreamClient
        {
            Response = ChatTextResponse("pong", "gpt-5.4")
        };
        var logs = new FakeProxyLogService();
        var service = CreateService(upstream, logs);
        var payload = new Dictionary<string, object?> { ["model"] = "m", ["input"] = "ping" };
        var upstreamRequest = new Dictionary<string, object?>
        {
            ["model"] = "gpt-5.4",
            ["messages"] = new List<object?>()
        };

        var result = await service.SendAsync(Context(
            payload,
            upstreamRequest,
            ProtocolConverter.Responses,
            ProtocolConverter.Chat));

        Assert.Equal(200, result.StatusCode);
        var responsePayload = Assert.IsType<Dictionary<string, object?>>(result.Payload);
        Assert.Equal("m", responsePayload["model"]);
        var output = Assert.IsType<List<object?>>(responsePayload["output"]);
        var message = Assert.IsType<Dictionary<string, object?>>(Assert.Single(output));
        var content = Assert.IsType<List<object?>>(message["content"]);
        var text = Assert.IsType<Dictionary<string, object?>>(Assert.Single(content));
        Assert.Equal("pong", text["text"]);

        var call = Assert.Single(upstream.Calls);
        Assert.Equal("chat", call.Channel["id"]);
        Assert.Same(upstreamRequest, call.Payload);
        Assert.Equal(30, call.DefaultTimeout);

        var log = Assert.Single(logs.Contexts);
        Assert.Equal("req_nonstream", log.Context.RequestId);
        Assert.Equal("alice", log.Context.OwnerUsername);
        Assert.Equal(12, log.Context.ApiKeyId);
        Assert.Same(payload, log.Context.Payload);
        Assert.Same(upstreamRequest, log.Context.UpstreamRequest);
        Assert.Same(upstream.Response, log.Context.UpstreamResponse);
        Assert.Same(responsePayload, log.Context.ResponsePayload);
        Assert.Null(log.Context.ErrorResponse);
        Assert.Equal("m", log.Context.RequestModel);
        Assert.Equal("gpt-5.4", log.Context.UpstreamModel);
        Assert.Equal("chat", log.Context.ChannelId);
        Assert.Equal(ProtocolConverter.Chat, log.Context.ChannelType);
        Assert.False(log.Context.IsStream);
        Assert.Null(log.Context.TtftMs);
        Assert.Equal(200, log.Context.StatusCode);
        Assert.Null(log.Context.Error);
        Assert.Null(log.Context.WebSearchDetails);
        Assert.Equal("POST", log.Request.Method);
        Assert.Equal("/v1/responses", log.Request.Path);
        Assert.Equal("203.0.113.10", log.Request.ClientIp);
    }

    [Fact]
    public async Task SendAsyncMapsProxyExceptionAndLogsFailure()
    {
        var upstream = new FakeUpstreamClient
        {
            Exception = new UpstreamException(
                "upstream returned HTTP 502",
                502,
                new Dictionary<string, object?> { ["error"] = "bad gateway" },
                "chat")
        };
        var logs = new FakeProxyLogService();
        var service = CreateService(upstream, logs);
        var upstreamRequest = new Dictionary<string, object?> { ["model"] = "gpt-5.4" };

        var result = await service.SendAsync(Context(
            new Dictionary<string, object?> { ["model"] = "m" },
            upstreamRequest,
            ProtocolConverter.Responses,
            ProtocolConverter.Chat));

        Assert.Equal(502, result.StatusCode);
        var resultPayload = JsonSerializer.Serialize(result.Payload);
        var error = JsonDocument.Parse(resultPayload).RootElement.GetProperty("error");
        Assert.Equal("upstream_error", error.GetProperty("type").GetString());
        Assert.Equal("chat", error.GetProperty("channel_id").GetString());

        var log = Assert.Single(logs.Contexts);
        Assert.Equal(502, log.Context.StatusCode);
        Assert.Equal("upstream returned HTTP 502", log.Context.Error);
        Assert.Same(upstreamRequest, log.Context.UpstreamRequest);
        Assert.Null(log.Context.UpstreamResponse);
        Assert.Null(log.Context.ResponsePayload);
        Assert.Same(result.Payload, log.Context.ErrorResponse);
    }

    private static ProxyNonStreamService CreateService(
        FakeUpstreamClient upstream,
        FakeProxyLogService logs)
    {
        return new ProxyNonStreamService(
            upstream,
            logs,
            new FakeWebSearchSimulator());
    }

    private static ProxyNonStreamContext Context(
        Dictionary<string, object?> payload,
        Dictionary<string, object?> upstreamRequest,
        string entryProtocol,
        string channelType)
    {
        return new ProxyNonStreamContext(
            Stopwatch.GetTimestamp(),
            "req_nonstream",
            "alice",
            12,
            payload,
            upstreamRequest,
            entryProtocol,
            new RouteResult(
                new Dictionary<string, object?>
                {
                    ["id"] = "chat",
                    ["type"] = channelType
                },
                "m",
                "gpt-5.4"),
            channelType,
            "chat",
            "user",
            "gpt-5.4",
            "m",
            30,
            new ProxyRequestMetadata(
                "POST",
                "/v1/responses",
                "203.0.113.10",
                new Dictionary<string, string>(StringComparer.Ordinal)),
            CancellationToken.None);
    }

    private static Dictionary<string, object?> ChatTextResponse(string text, string model)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_1",
            ["model"] = model,
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = text
                    },
                    ["finish_reason"] = "stop"
                }
            }
        };
    }

    private sealed class FakeUpstreamClient : IUpstreamClient
    {
        public Dictionary<string, object?> Response { get; init; } = [];

        public UpstreamException? Exception { get; init; }

        public List<UpstreamCall> Calls { get; } = [];

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new UpstreamCall(
                channel.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                payload,
                defaultTimeout));

            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Response);
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed record UpstreamCall(
        Dictionary<string, object?> Channel,
        IReadOnlyDictionary<string, object?> Payload,
        int DefaultTimeout);

    private sealed class FakeProxyLogService : IProxyLogService
    {
        public List<LogCall> Contexts { get; } = [];

        public long WriteLog(ProxyLogContext context, ProxyRequestMetadata request)
        {
            Contexts.Add(new LogCall(context, request));
            return Contexts.Count;
        }

        public long WriteLog(ProxyRequestLogContext context)
        {
            throw new NotSupportedException();
        }
    }

    private sealed record LogCall(
        ProxyLogContext Context,
        ProxyRequestMetadata Request);

    private sealed class FakeWebSearchSimulator : IWebSearchSimulator
    {
        public bool CanSimulate(
            string entryProtocol,
            string channelType,
            string ownerRole,
            IReadOnlyDictionary<string, object?> payload)
        {
            return false;
        }

        public Task<WebSearchSimulationResult> RunAsync(
            IReadOnlyDictionary<string, object?> channel,
            Dictionary<string, object?> upstreamRequest,
            Dictionary<string, object?> payload,
            string? originalModel,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<string> RunChatStreamAsync(
            IReadOnlyDictionary<string, object?> channel,
            Dictionary<string, object?> upstreamRequest,
            Dictionary<string, object?> payload,
            string? originalModel,
            int defaultTimeout,
            WebSearchStreamResult result,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

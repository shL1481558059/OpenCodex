using System.Diagnostics;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Protocols;
using OpenCodex.Api.Routing;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Proxy;

public sealed class ProxyStreamServiceTests
{
    [Fact]
    public async Task StreamAsyncWritesSameProtocolStreamAndLogsPassthrough()
    {
        var upstream = new FakeUpstreamClient
        {
            StreamLines =
            [
                "data: {\"delta\":\"po\"}\n",
                "\n",
                "data: [DONE]\n"
            ]
        };
        var logs = new FakeProxyLogService();
        var service = new ProxyStreamService(
            upstream,
            logs,
            new FakeWebSearchSimulator());
        var streamWriter = new FakeProxyStreamWriter();
        var payload = new Dictionary<string, object?> { ["model"] = "m", ["stream"] = true };
        var upstreamRequest = new Dictionary<string, object?> { ["model"] = "upstream-model" };
        var requestMetadata = new ProxyRequestMetadata(
            "POST",
            "/v1/chat/completions",
            "203.0.113.10",
            new Dictionary<string, string>(StringComparer.Ordinal));

        await service.StreamAsync(new ProxyStreamContext(
            Stopwatch.GetTimestamp(),
            "req_stream",
            "alice",
            12,
            payload,
            upstreamRequest,
            ProtocolConverter.Chat,
            new RouteResult(
                new Dictionary<string, object?>
                {
                    ["id"] = "chat",
                    ["type"] = ProtocolConverter.Chat
                },
                "m",
                "upstream-model"),
            ProtocolConverter.Chat,
            "chat",
            "user",
            "upstream-model",
            "m",
            30,
            requestMetadata,
            streamWriter,
            CancellationToken.None));

        Assert.True(streamWriter.Prepared);
        Assert.Equal("data: {\"delta\":\"po\"}\n\ndata: [DONE]\n", streamWriter.Body);
        Assert.NotNull(streamWriter.TtftMs);

        var call = Assert.Single(upstream.Calls);
        Assert.Equal("chat", call.Channel["id"]);
        Assert.Equal("upstream-model", call.Payload["model"]);
        Assert.True(call.Payload["stream"] is true);
        Assert.Equal(30, call.DefaultTimeout);

        var log = Assert.Single(logs.Contexts);
        Assert.Equal("req_stream", log.Context.RequestId);
        Assert.Equal("alice", log.Context.OwnerUsername);
        Assert.Equal(12, log.Context.ApiKeyId);
        Assert.Same(payload, log.Context.Payload);
        Assert.Same(upstreamRequest, log.Context.UpstreamRequest);
        Assert.Null(log.Context.UpstreamResponse);
        Assert.Null(log.Context.ResponsePayload);
        Assert.Equal("m", log.Context.RequestModel);
        Assert.Equal("upstream-model", log.Context.UpstreamModel);
        Assert.Equal("chat", log.Context.ChannelId);
        Assert.Equal(ProtocolConverter.Chat, log.Context.ChannelType);
        Assert.True(log.Context.IsStream);
        Assert.Equal(streamWriter.TtftMs, log.Context.TtftMs);
        Assert.Equal(200, log.Context.StatusCode);
        Assert.Null(log.Context.Error);
        Assert.Equal("POST", log.Request.Method);
        Assert.Equal("/v1/chat/completions", log.Request.Path);
        Assert.Equal("203.0.113.10", log.Request.ClientIp);
    }

    private sealed class FakeUpstreamClient : IUpstreamClient
    {
        public IReadOnlyList<string> StreamLines { get; init; } = [];

        public List<UpstreamCall> Calls { get; } = [];

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add(new UpstreamCall(
                channel.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                payload.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                defaultTimeout));

            foreach (var line in StreamLines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return line;
            }
        }
    }

    private sealed record UpstreamCall(
        Dictionary<string, object?> Channel,
        Dictionary<string, object?> Payload,
        int DefaultTimeout);

    private sealed class FakeProxyStreamWriter : IProxyStreamWriter
    {
        private readonly StringWriter _body = new();

        public bool Prepared { get; private set; }

        public int? TtftMs { get; private set; }

        public string Body => _body.ToString();

        public void PrepareSse()
        {
            Prepared = true;
        }

        public async Task<int?> WriteLinesAsync(
            IAsyncEnumerable<string> lines,
            Func<string, bool> countsForTtft,
            Func<int> elapsedMilliseconds,
            CancellationToken cancellationToken = default)
        {
            await foreach (var line in lines.WithCancellation(cancellationToken))
            {
                if (TtftMs is null && countsForTtft(line))
                {
                    TtftMs = elapsedMilliseconds();
                }

                await _body.WriteAsync(line);
            }

            return TtftMs;
        }
    }

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

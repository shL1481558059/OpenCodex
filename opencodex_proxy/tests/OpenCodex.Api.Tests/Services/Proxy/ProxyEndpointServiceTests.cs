using System.Text.Json;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Protocols;
using OpenCodex.Api.Routing;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Proxy;

public sealed class ProxyEndpointServiceTests
{
    [Fact]
    public async Task ProxyAsyncBuildsNonStreamContextAndReturnsServiceResult()
    {
        var payload = new Dictionary<string, object?> { ["model"] = "m", ["input"] = "ping" };
        var requests = new FakeProxyRequestService();
        var routes = new FakeProxyRouteService();
        var nonStreams = new FakeProxyNonStreamService
        {
            Result = new ProxyNonStreamResult(
                202,
                new Dictionary<string, object?> { ["ok"] = true })
        };
        var streams = new FakeProxyStreamService();
        var logs = new FakeProxyLogService();
        var service = CreateService(logs, requests, routes, nonStreams, streams);

        var result = await service.ProxyAsync(Context(
            ProtocolConverter.Responses,
            payload,
            authorizationHeader: "Bearer ocx_secret"));

        Assert.False(result.IsEmpty);
        Assert.Equal(202, result.StatusCode);
        Assert.Same(nonStreams.Result.Payload, result.Payload);
        Assert.Empty(logs.Contexts);
        Assert.Empty(streams.Contexts);
        Assert.Equal(["Bearer ocx_secret"], requests.AuthorizationHeaderCalls);

        Assert.Equal([("alice", "m")], routes.Calls);
        var nonStream = Assert.Single(nonStreams.Contexts);
        Assert.Equal("req_endpoint", nonStream.RequestId);
        Assert.Equal("alice", nonStream.OwnerUsername);
        Assert.Equal(12, nonStream.ApiKeyId);
        Assert.Same(payload, nonStream.Payload);
        Assert.Equal(ProtocolConverter.Responses, nonStream.EntryProtocol);
        Assert.Equal(ProtocolConverter.Chat, nonStream.ChannelType);
        Assert.Equal("chat", nonStream.ChannelId);
        Assert.Equal("superadmin", nonStream.OwnerRole);
        Assert.Equal("gpt-5.4", nonStream.UpstreamModel);
        Assert.Equal("m", nonStream.RequestModel);
        Assert.Equal(30, nonStream.DefaultTimeout);
        Assert.Equal("POST", nonStream.RequestMetadata.Method);
        Assert.Equal("/v1/responses", nonStream.RequestMetadata.Path);
        Assert.Equal("203.0.113.10", nonStream.RequestMetadata.ClientIp);
        Assert.Equal("gpt-5.4", nonStream.UpstreamRequest["model"]);
        Assert.True(nonStream.UpstreamRequest.ContainsKey("messages"));
    }

    [Fact]
    public async Task ProxyAsyncBuildsStreamContextAndReturnsEmptyResult()
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = "m",
            ["messages"] = new List<object?>(),
            ["stream"] = true
        };
        var requests = new FakeProxyRequestService();
        var routes = new FakeProxyRouteService
        {
            Route = new RouteResult(
                new Dictionary<string, object?>
                {
                    ["id"] = "chat",
                    ["type"] = ProtocolConverter.Chat
                },
                "m",
                "gpt-5.4")
        };
        var nonStreams = new FakeProxyNonStreamService();
        var streams = new FakeProxyStreamService();
        var logs = new FakeProxyLogService();
        var service = CreateService(logs, requests, routes, nonStreams, streams);

        var result = await service.ProxyAsync(Context(ProtocolConverter.Chat, payload));

        Assert.True(result.IsEmpty);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Payload);
        Assert.Empty(logs.Contexts);
        Assert.Empty(nonStreams.Contexts);

        var stream = Assert.Single(streams.Contexts);
        Assert.Equal("req_endpoint", stream.RequestId);
        Assert.Equal("alice", stream.OwnerUsername);
        Assert.Equal(12, stream.ApiKeyId);
        Assert.Same(payload, stream.Payload);
        Assert.Equal(ProtocolConverter.Chat, stream.EntryProtocol);
        Assert.Equal(ProtocolConverter.Chat, stream.ChannelType);
        Assert.Equal("chat", stream.ChannelId);
        Assert.Equal("superadmin", stream.OwnerRole);
        Assert.Equal("gpt-5.4", stream.UpstreamModel);
        Assert.Equal("m", stream.RequestModel);
        Assert.Equal("POST", stream.RequestMetadata.Method);
        Assert.Equal("/v1/responses", stream.RequestMetadata.Path);
        Assert.Equal("203.0.113.10", stream.RequestMetadata.ClientIp);
        Assert.IsType<FakeProxyStreamWriter>(stream.StreamWriter);
        Assert.True(stream.UpstreamRequest["stream"] is true);
    }

    [Fact]
    public async Task ProxyAsyncLogsEarlyFailureWhenBodyIsNotJsonObject()
    {
        var requests = new FakeProxyRequestService();
        var routes = new FakeProxyRouteService();
        var nonStreams = new FakeProxyNonStreamService();
        var streams = new FakeProxyStreamService();
        var logs = new FakeProxyLogService();
        var service = CreateService(logs, requests, routes, nonStreams, streams);

        var result = await service.ProxyAsync(Context(ProtocolConverter.Responses, payload: null));

        Assert.False(result.IsEmpty);
        Assert.Equal(400, result.StatusCode);
        var errorPayload = JsonSerializer.Serialize(result.Payload);
        var error = JsonDocument.Parse(errorPayload).RootElement.GetProperty("error");
        Assert.Equal("request body must be a JSON object", error.GetProperty("message").GetString());
        Assert.Equal("bad_request", error.GetProperty("type").GetString());
        Assert.Empty(routes.Calls);
        Assert.Empty(nonStreams.Contexts);
        Assert.Empty(streams.Contexts);

        var log = Assert.Single(logs.Contexts);
        Assert.Equal("req_endpoint", log.Context.RequestId);
        Assert.Equal("alice", log.Context.OwnerUsername);
        Assert.Equal(12, log.Context.ApiKeyId);
        Assert.Null(log.Context.Payload);
        Assert.Null(log.Context.UpstreamRequest);
        Assert.Null(log.Context.UpstreamResponse);
        Assert.Null(log.Context.ResponsePayload);
        Assert.Same(result.Payload, log.Context.ErrorResponse);
        Assert.Null(log.Context.RequestModel);
        Assert.Null(log.Context.ChannelType);
        Assert.Equal(400, log.Context.StatusCode);
        Assert.Equal("request body must be a JSON object", log.Context.Error);
        Assert.Equal("POST", log.Request.Method);
        Assert.Equal("/v1/responses", log.Request.Path);
        Assert.Equal("203.0.113.10", log.Request.ClientIp);
    }

    private static ProxyEndpointService CreateService(
        FakeProxyLogService logs,
        FakeProxyRequestService requests,
        FakeProxyRouteService routes,
        FakeProxyNonStreamService nonStreams,
        FakeProxyStreamService streams)
    {
        return new ProxyEndpointService(logs, requests, routes, nonStreams, streams);
    }

    private static ProxyEndpointContext Context(
        string entryProtocol,
        Dictionary<string, object?>? payload,
        string? authorizationHeader = null)
    {
        return new ProxyEndpointContext(
            entryProtocol,
            payload,
            authorizationHeader,
            new ProxyRequestMetadata(
                "POST",
                "/v1/responses",
                "203.0.113.10",
                new Dictionary<string, string>(StringComparer.Ordinal)),
            new FakeProxyStreamWriter(),
            CancellationToken.None);
    }

    private sealed class FakeProxyRequestService : IProxyRequestService
    {
        public List<string?> AuthorizationHeaderCalls { get; } = [];

        public ProxyRequestState StartRequest()
        {
            return new ProxyRequestState("req_endpoint", "admin", 30);
        }

        public AuthenticatedAccessApiKeyRecord AuthenticateAccessKey(string? authorizationHeader)
        {
            AuthorizationHeaderCalls.Add(authorizationHeader);
            return new AuthenticatedAccessApiKeyRecord(
                12,
                "alice",
                "test",
                "sk",
                "tail",
                "sk...tail",
                true,
                1,
                2,
                null,
                new AccessApiKeyUserRecord("alice", "superadmin", true));
        }
    }

    private sealed class FakeProxyStreamWriter : IProxyStreamWriter
    {
        public void PrepareSse()
        {
        }

        public Task<int?> WriteLinesAsync(
            IAsyncEnumerable<string> lines,
            Func<string, bool> countsForTtft,
            Func<int> elapsedMilliseconds,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeProxyRouteService : IProxyRouteService
    {
        public RouteResult Route { get; init; } = new(
            new Dictionary<string, object?>
            {
                ["id"] = "chat",
                ["type"] = ProtocolConverter.Chat
            },
            "m",
            "gpt-5.4");

        public List<(string OwnerUsername, string? Model)> Calls { get; } = [];

        public RouteResult ChooseRoute(string ownerUsername, string? model)
        {
            Calls.Add((ownerUsername, model));
            return Route;
        }
    }

    private sealed class FakeProxyNonStreamService : IProxyNonStreamService
    {
        public ProxyNonStreamResult Result { get; init; } = new(
            200,
            new Dictionary<string, object?>());

        public List<ProxyNonStreamContext> Contexts { get; } = [];

        public Task<ProxyNonStreamResult> SendAsync(ProxyNonStreamContext context)
        {
            Contexts.Add(context);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeProxyStreamService : IProxyStreamService
    {
        public List<ProxyStreamContext> Contexts { get; } = [];

        public Task StreamAsync(ProxyStreamContext context)
        {
            Contexts.Add(context);
            return Task.CompletedTask;
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
}

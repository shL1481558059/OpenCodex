using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyEndpointServiceTests
{
    [Fact]
    public async Task ProxyAsync_SamePriorityPrefersLessBusyChannel()
    {
        var capacity = new ChannelCapacityService();
        var busyChannel = CreateChannel("busy", priority: 1);
        var idleChannel = CreateChannel("idle", priority: 1);
        using var busyLease = capacity.TryAcquire("admin", busyChannel);

        var nonStreams = new StubProxyNonStreamService(_ =>
            Task.FromResult(new ProxyNonStreamResult(200, new { ok = true })));
        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(busyChannel, "shared-model", "upstream-busy"),
                CreateRoute(idleChannel, "shared-model", "upstream-idle")
            ]),
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(CreateChatContext("shared-model"));

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(nonStreams.LastContext);
        Assert.Equal("idle", nonStreams.LastContext!.ChannelId);
        Assert.Equal("upstream-idle", nonStreams.LastContext.UpstreamModel);
        Assert.Equal(1, capacity.GetActiveRequests("admin", "busy"));
        Assert.Equal(0, capacity.GetActiveRequests("admin", "idle"));
    }

    [Fact]
    public async Task ProxyAsync_AllCandidatesAtCapacity_ReturnsTooManyRequests()
    {
        var capacity = new ChannelCapacityService();
        var primary = CreateChannel("primary", priority: 0, capacity: 1);
        var secondary = CreateChannel("secondary", priority: 1, capacity: 1);
        using var primaryLease = capacity.TryAcquire("admin", primary);
        using var secondaryLease = capacity.TryAcquire("admin", secondary);

        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(primary, "shared-model", "upstream-primary"),
                CreateRoute(secondary, "shared-model", "upstream-secondary")
            ]));

        var result = await service.ProxyAsync(CreateChatContext("shared-model"));

        Assert.Equal(429, result.StatusCode);
        Assert.Equal(1, capacity.GetActiveRequests("admin", "primary"));
        Assert.Equal(1, capacity.GetActiveRequests("admin", "secondary"));
    }

    [Fact]
    public async Task ProxyAsync_StickyKeyRoutesToPreviouslyRememberedChannel()
    {
        var capacity = new ChannelCapacityService();
        var affinity = new ChannelAffinityService();
        var first = CreateChannel("first", priority: 1);
        var second = CreateChannel("second", priority: 1);
        // 预置亲和映射：该 sticky key 此前命中 "second"。
        affinity.Remember("admin", "cache-key-1", "second");

        var nonStreams = new StubProxyNonStreamService(_ =>
            Task.FromResult(new ProxyNonStreamResult(200, new { ok = true })));
        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(first, "shared-model", "upstream-first"),
                CreateRoute(second, "shared-model", "upstream-second")
            ]),
            affinity: affinity,
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(
            CreateChatContext(CreateChatPayload("shared-model", "cache-key-1")));

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(nonStreams.LastContext);
        Assert.Equal("second", nonStreams.LastContext!.ChannelId);
    }

    [Fact]
    public async Task ProxyAsync_StickyPreferredChannelAtCapacity_FallsBackToOtherChannel()
    {
        var capacity = new ChannelCapacityService();
        var affinity = new ChannelAffinityService();
        var preferred = CreateChannel("preferred", priority: 1, capacity: 1);
        var fallback = CreateChannel("fallback", priority: 1);
        // 偏好渠道已被占满，软粘应回退到其他渠道而非报 429。
        using var preferredLease = capacity.TryAcquire("admin", preferred);
        affinity.Remember("admin", "cache-key-2", "preferred");

        var nonStreams = new StubProxyNonStreamService(_ =>
            Task.FromResult(new ProxyNonStreamResult(200, new { ok = true })));
        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(preferred, "shared-model", "upstream-preferred"),
                CreateRoute(fallback, "shared-model", "upstream-fallback")
            ]),
            affinity: affinity,
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(
            CreateChatContext(CreateChatPayload("shared-model", "cache-key-2")));

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(nonStreams.LastContext);
        Assert.Equal("fallback", nonStreams.LastContext!.ChannelId);
        // 回退后亲和映射应更新为实际命中的渠道。
        Assert.Equal("fallback", affinity.GetPreferredChannelId("admin", "cache-key-2"));
    }

    [Fact]
    public async Task ProxyAsync_NonStreamSuccess_ReleasesCapacity()
    {
        var capacity = new ChannelCapacityService();
        var channel = CreateChannel("nonstream-success", priority: 0, capacity: 1);
        var service = CreateService(
            capacity,
            new StubProxyRouteService([CreateRoute(channel, "shared-model", "upstream")]),
            nonStreams: new StubProxyNonStreamService(_ =>
                Task.FromResult(new ProxyNonStreamResult(200, new { ok = true }))));

        var result = await service.ProxyAsync(CreateChatContext("shared-model"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(0, capacity.GetActiveRequests("admin", "nonstream-success"));
    }

    [Fact]
    public async Task ProxyAsync_NonStreamFailure_ReleasesCapacity()
    {
        var capacity = new ChannelCapacityService();
        var channel = CreateChannel("nonstream-failure", priority: 0, capacity: 1);
        var service = CreateService(
            capacity,
            new StubProxyRouteService([CreateRoute(channel, "shared-model", "upstream")]),
            nonStreams: new StubProxyNonStreamService(_ =>
                throw new UpstreamException("upstream failed")));

        var result = await service.ProxyAsync(CreateChatContext("shared-model"));

        Assert.Equal(502, result.StatusCode);
        Assert.Equal(0, capacity.GetActiveRequests("admin", "nonstream-failure"));
    }

    [Fact]
    public async Task ProxyAsync_NonStreamRetryableFailure_FailsOverToNextChannel()
    {
        var capacity = new ChannelCapacityService();
        var primary = CreateChannel("primary", priority: 0, capacity: 1);
        var secondary = CreateChannel("secondary", priority: 1, capacity: 1);
        var attempts = new List<string>();
        var nonStreams = new StubProxyNonStreamService(context =>
        {
            attempts.Add(context.ChannelId);
            if (context.ChannelId == "primary")
            {
                throw new UpstreamException("primary unavailable", ProxyHttpStatus.BadGateway);
            }

            return Task.FromResult(new ProxyNonStreamResult(200, new { ok = true }));
        });
        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(primary, "shared-model", "upstream-primary"),
                CreateRoute(secondary, "shared-model", "upstream-secondary")
            ]),
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(CreateChatContext("shared-model"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(["primary", "secondary"], attempts);
        Assert.Equal("secondary", nonStreams.LastContext!.ChannelId);
        Assert.Equal(0, capacity.GetActiveRequests("admin", "primary"));
        Assert.Equal(0, capacity.GetActiveRequests("admin", "secondary"));
    }

    [Fact]
    public async Task ProxyAsync_NonStreamUpstreamBadRequest_FailsOverToNextChannel()
    {
        var capacity = new ChannelCapacityService();
        var primary = CreateChannel("primary", priority: 0, capacity: 1);
        var secondary = CreateChannel("secondary", priority: 1, capacity: 1);
        var attempts = new List<string>();
        var nonStreams = new StubProxyNonStreamService(context =>
        {
            attempts.Add(context.ChannelId);
            if (context.ChannelId == "primary")
            {
                throw new UpstreamException("primary rejected payload", ProxyHttpStatus.BadRequest);
            }

            return Task.FromResult(new ProxyNonStreamResult(200, new { ok = true }));
        });
        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(primary, "shared-model", "upstream-primary"),
                CreateRoute(secondary, "shared-model", "upstream-secondary")
            ]),
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(CreateChatContext("shared-model"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(["primary", "secondary"], attempts);
        Assert.Equal("secondary", nonStreams.LastContext!.ChannelId);
        Assert.Equal(0, capacity.GetActiveRequests("admin", "primary"));
        Assert.Equal(0, capacity.GetActiveRequests("admin", "secondary"));
    }

    [Fact]
    public async Task ProxyAsync_NonStreamFailureResult_FailsOverToNextChannel()
    {
        var capacity = new ChannelCapacityService();
        var breaker = new ChannelCircuitBreakerService();
        var primary = CreateChannel("primary", priority: 0, capacity: 1);
        var secondary = CreateChannel("secondary", priority: 1, capacity: 1);
        var attempts = new List<string>();
        var nonStreams = new StubProxyNonStreamService(context =>
        {
            attempts.Add(context.ChannelId);
            return context.ChannelId == "primary"
                ? Task.FromResult(new ProxyNonStreamResult(
                    ProxyHttpStatus.BadGateway,
                    new { error = true },
                    new UpstreamException("primary unavailable", ProxyHttpStatus.BadGateway)))
                : Task.FromResult(new ProxyNonStreamResult(200, new { ok = true }));
        });
        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(primary, "shared-model", "upstream-primary"),
                CreateRoute(secondary, "shared-model", "upstream-secondary")
            ]),
            breaker: breaker,
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(CreateChatContext("shared-model"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(["primary", "secondary"], attempts);
        Assert.Equal(ChannelHealthStatus.Healthy, breaker.GetHealthStatus("admin", "secondary", enabled: true));
    }

    [Fact]
    public async Task ProxyAsync_NonStreamBadRequest_DoesNotFailOver()
    {
        var capacity = new ChannelCapacityService();
        var primary = CreateChannel("primary", priority: 0, capacity: 1);
        var secondary = CreateChannel("secondary", priority: 1, capacity: 1);
        var attempts = new List<string>();
        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(primary, "shared-model", "upstream-primary"),
                CreateRoute(secondary, "shared-model", "upstream-secondary")
            ]),
            nonStreams: new StubProxyNonStreamService(context =>
            {
                attempts.Add(context.ChannelId);
                throw new BadRequestException("bad payload");
            }));

        var result = await service.ProxyAsync(CreateChatContext("shared-model"));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal(["primary"], attempts);
        Assert.Equal(0, capacity.GetActiveRequests("admin", "primary"));
        Assert.Equal(0, capacity.GetActiveRequests("admin", "secondary"));
    }

    [Fact]
    public async Task ProxyAsync_StreamSuccess_ReleasesCapacity()
    {
        var capacity = new ChannelCapacityService();
        var channel = CreateChannel("stream-success", priority: 0, capacity: 1);
        var streams = new StubProxyStreamService(_ => Task.CompletedTask);
        var service = CreateService(
            capacity,
            new StubProxyRouteService([CreateRoute(channel, "shared-model", "upstream")]),
            streams: streams);

        var payload = CreateChatPayload("shared-model");
        payload["stream"] = true;
        var result = await service.ProxyAsync(CreateChatContext(payload));

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.IsEmpty);
        Assert.NotNull(streams.LastContext);
        Assert.Equal("stream-success", streams.LastContext!.ChannelId);
        Assert.Equal(0, capacity.GetActiveRequests("admin", "stream-success"));
    }

    [Fact]
    public async Task ProxyAsync_StreamFailure_ReleasesCapacity()
    {
        var capacity = new ChannelCapacityService();
        var channel = CreateChannel("stream-failure", priority: 0, capacity: 1);
        var service = CreateService(
            capacity,
            new StubProxyRouteService([CreateRoute(channel, "shared-model", "upstream")]),
            streams: new StubProxyStreamService(_ => throw new UpstreamException("stream failed")));

        var payload = CreateChatPayload("shared-model");
        payload["stream"] = true;
        var result = await service.ProxyAsync(CreateChatContext(payload));

        Assert.Equal(502, result.StatusCode);
        Assert.Equal(0, capacity.GetActiveRequests("admin", "stream-failure"));
    }

    [Fact]
    public async Task ProxyAsync_StreamRetryableFailureBeforeFirstByte_FailsOverToNextChannel()
    {
        var capacity = new ChannelCapacityService();
        var primary = CreateChannel("primary", priority: 0, capacity: 1);
        var secondary = CreateChannel("secondary", priority: 1, capacity: 1);
        var attempts = new List<string>();
        var streams = new StubProxyStreamService(async context =>
        {
            attempts.Add(context.ChannelId);
            if (context.ChannelId == "primary")
            {
                throw new UpstreamException("stream timeout", ProxyHttpStatus.GatewayTimeout);
            }

            await context.StreamWriter.WriteLinesAsync(
                ToAsyncEnumerable("data: {\"type\":\"response.created\"}\n\n"),
                static _ => true,
                static () => 1,
                CancellationToken.None);
        });
        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(primary, "shared-model", "upstream-primary"),
                CreateRoute(secondary, "shared-model", "upstream-secondary")
            ]),
            streams: streams);

        var payload = CreateChatPayload("shared-model");
        payload["stream"] = true;
        var writer = new RecordingProxyStreamWriter();
        var result = await service.ProxyAsync(CreateChatContext(payload, writer));

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.IsEmpty);
        Assert.Equal(["primary", "secondary"], attempts);
        Assert.Single(writer.WrittenLines);
        Assert.Equal(0, capacity.GetActiveRequests("admin", "primary"));
        Assert.Equal(0, capacity.GetActiveRequests("admin", "secondary"));
    }

    [Fact]
    public async Task ProxyAsync_StreamUpstreamBadRequestBeforeFirstByte_FailsOverToNextChannel()
    {
        var capacity = new ChannelCapacityService();
        var primary = CreateChannel("primary", priority: 0, capacity: 1);
        var secondary = CreateChannel("secondary", priority: 1, capacity: 1);
        var attempts = new List<string>();
        var streams = new StubProxyStreamService(async context =>
        {
            attempts.Add(context.ChannelId);
            if (context.ChannelId == "primary")
            {
                throw new UpstreamException("primary rejected stream payload", ProxyHttpStatus.BadRequest);
            }

            await context.StreamWriter.WriteLinesAsync(
                ToAsyncEnumerable("data: {\"type\":\"response.created\"}\n\n"),
                static _ => true,
                static () => 1,
                CancellationToken.None);
        });
        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(primary, "shared-model", "upstream-primary"),
                CreateRoute(secondary, "shared-model", "upstream-secondary")
            ]),
            streams: streams);

        var payload = CreateChatPayload("shared-model");
        payload["stream"] = true;
        var writer = new RecordingProxyStreamWriter();
        var result = await service.ProxyAsync(CreateChatContext(payload, writer));

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.IsEmpty);
        Assert.Equal(["primary", "secondary"], attempts);
        Assert.Single(writer.WrittenLines);
        Assert.Equal(0, capacity.GetActiveRequests("admin", "primary"));
        Assert.Equal(0, capacity.GetActiveRequests("admin", "secondary"));
    }

    [Fact]
    public async Task ProxyAsync_StreamRetryableFailureAfterFirstByte_DoesNotFailOver()
    {
        var capacity = new ChannelCapacityService();
        var primary = CreateChannel("primary", priority: 0, capacity: 1);
        var secondary = CreateChannel("secondary", priority: 1, capacity: 1);
        var attempts = new List<string>();
        var streams = new StubProxyStreamService(async context =>
        {
            attempts.Add(context.ChannelId);
            await context.StreamWriter.WriteLinesAsync(
                ToAsyncEnumerable("data: {\"type\":\"response.created\"}\n\n"),
                static _ => true,
                static () => 1,
                CancellationToken.None);
            throw new UpstreamException("stream dropped", ProxyHttpStatus.BadGateway);
        });
        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(primary, "shared-model", "upstream-primary"),
                CreateRoute(secondary, "shared-model", "upstream-secondary")
            ]),
            streams: streams);

        var payload = CreateChatPayload("shared-model");
        payload["stream"] = true;
        var writer = new RecordingProxyStreamWriter();

        await Assert.ThrowsAsync<UpstreamException>(() => service.ProxyAsync(CreateChatContext(payload, writer)));

        Assert.Equal(["primary"], attempts);
        Assert.Single(writer.WrittenLines);
        Assert.Equal(0, capacity.GetActiveRequests("admin", "primary"));
        Assert.Equal(0, capacity.GetActiveRequests("admin", "secondary"));
    }

    [Fact]
    public async Task ProxyAsync_OpenCircuit_SkipsPrimaryChannel()
    {
        var capacity = new ChannelCapacityService();
        var now = DateTimeOffset.UtcNow;
        var breaker = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(30),
            halfOpenMaxProbeRequests: 1,
            clock: () => now);
        var primary = CreateChannel("primary", priority: 0, capacity: 1);
        var secondary = CreateChannel("secondary", priority: 1, capacity: 1);
        breaker.RecordFailure("admin", "primary", new UpstreamException("down", ProxyHttpStatus.BadGateway));

        var attempts = new List<string>();
        var nonStreams = new StubProxyNonStreamService(context =>
        {
            attempts.Add(context.ChannelId);
            return Task.FromResult(new ProxyNonStreamResult(200, new { ok = true }));
        });
        var service = CreateService(
            capacity,
            new StubProxyRouteService(
            [
                CreateRoute(primary, "shared-model", "upstream-primary"),
                CreateRoute(secondary, "shared-model", "upstream-secondary")
            ]),
            breaker: breaker,
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(CreateChatContext("shared-model"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(["secondary"], attempts);
    }

    [Fact]
    public async Task ProxyAsync_HalfOpenProbeSuccess_ClosesCircuit()
    {
        var capacity = new ChannelCapacityService();
        var now = DateTimeOffset.UtcNow;
        var breaker = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            clock: () => now);
        var primary = CreateChannel("primary", priority: 0, capacity: 1);
        breaker.RecordFailure("admin", "primary", new UpstreamException("down", ProxyHttpStatus.BadGateway));
        now = now.AddSeconds(11);

        var nonStreams = new StubProxyNonStreamService(_ =>
            Task.FromResult(new ProxyNonStreamResult(200, new { ok = true })));
        var service = CreateService(
            capacity,
            new StubProxyRouteService([CreateRoute(primary, "shared-model", "upstream-primary")]),
            breaker: breaker,
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(CreateChatContext("shared-model"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(ChannelHealthStatus.Healthy, breaker.GetHealthStatus("admin", "primary", enabled: true));
    }

    [Fact]
    public async Task ProxyAsync_ResponsesPassthrough_CopiesCodexHeadersToUpstreamChannel()
    {
        var capacity = new ChannelCapacityService();
        var channel = CreateChannel("responses", priority: 0);
        channel["type"] = ProtocolConverter.Responses;
        var nonStreams = new StubProxyNonStreamService(_ =>
            Task.FromResult(new ProxyNonStreamResult(200, new { ok = true })));
        var service = CreateService(
            capacity,
            new StubProxyRouteService([CreateRoute(channel, "shared-model", "upstream")]),
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(CreateResponsesContext(
            "shared-model",
            new Dictionary<string, string>
            {
                ["User-Agent"] = "Codex Desktop/0.140.0-alpha.2",
                ["x-oai-attestation"] = "attestation-token",
                ["x-codex-turn-metadata"] = "turn-metadata",
                ["x-codex-window-id"] = "window-id",
                ["x-client-request-id"] = "request-id",
                ["originator"] = "Codex Desktop",
                ["session-id"] = "session-id",
                ["thread-id"] = "thread-id",
                ["Authorization"] = "Bearer proxy-key"
            }));

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(nonStreams.LastContext);
        var headers = Assert.IsType<Dictionary<string, object?>>(nonStreams.LastContext!.Route.Channel["headers"]);
        Assert.Equal("Codex Desktop/0.140.0-alpha.2", headers["User-Agent"]);
        Assert.Equal("attestation-token", headers["x-oai-attestation"]);
        Assert.Equal("turn-metadata", headers["x-codex-turn-metadata"]);
        Assert.Equal("window-id", headers["x-codex-window-id"]);
        Assert.Equal("request-id", headers["x-client-request-id"]);
        Assert.Equal("Codex Desktop", headers["originator"]);
        Assert.Equal("session-id", headers["session-id"]);
        Assert.Equal("thread-id", headers["thread-id"]);
        Assert.False(headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task ProxyAsync_ResponsesPassthrough_AddsDefaultCodexHeadersWhenMissing()
    {
        var capacity = new ChannelCapacityService();
        var channel = CreateChannel("responses", priority: 0);
        channel["type"] = ProtocolConverter.Responses;
        var nonStreams = new StubProxyNonStreamService(_ =>
            Task.FromResult(new ProxyNonStreamResult(200, new { ok = true })));
        var service = CreateService(
            capacity,
            new StubProxyRouteService([CreateRoute(channel, "shared-model", "upstream")]),
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(CreateResponsesContext(
            "shared-model",
            new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0"
            }));

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(nonStreams.LastContext);
        var headers = Assert.IsType<Dictionary<string, object?>>(nonStreams.LastContext!.Route.Channel["headers"]);
        Assert.Contains("Codex Desktop", Assert.IsType<string>(headers["User-Agent"]));
        Assert.Equal("test-attestation", headers["x-oai-attestation"]);
        Assert.Equal("Codex Desktop", headers["originator"]);
        Assert.Equal("test-window", headers["x-codex-window-id"]);
        Assert.Equal("test-request", headers["x-client-request-id"]);
        Assert.Equal("test-session", headers["session-id"]);
        Assert.Equal("test-thread", headers["thread-id"]);
    }

    [Fact]
    public async Task ProxyAsync_ResponsesToChat_DoesNotCopyCodexHeaders()
    {
        var capacity = new ChannelCapacityService();
        var channel = CreateChannel("chat", priority: 0);
        var nonStreams = new StubProxyNonStreamService(_ =>
            Task.FromResult(new ProxyNonStreamResult(200, new { ok = true })));
        var service = CreateService(
            capacity,
            new StubProxyRouteService([CreateRoute(channel, "shared-model", "upstream")]),
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(CreateResponsesContext(
            "shared-model",
            new Dictionary<string, string>
            {
                ["User-Agent"] = "Codex Desktop/0.140.0-alpha.2",
                ["x-oai-attestation"] = "attestation-token"
            }));

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(nonStreams.LastContext);
        Assert.False(nonStreams.LastContext!.Route.Channel.ContainsKey("headers"));
    }

    [Fact]
    public async Task ProxyAsync_ResponsesPassthrough_DoesNotReplaceConfiguredHeaders()
    {
        var capacity = new ChannelCapacityService();
        var channel = CreateChannel("responses", priority: 0);
        channel["type"] = ProtocolConverter.Responses;
        channel["headers"] = new Dictionary<string, object?>
        {
            ["User-Agent"] = "configured-agent",
            ["x-oai-attestation"] = "configured-attestation"
        };
        var nonStreams = new StubProxyNonStreamService(_ =>
            Task.FromResult(new ProxyNonStreamResult(200, new { ok = true })));
        var service = CreateService(
            capacity,
            new StubProxyRouteService([CreateRoute(channel, "shared-model", "upstream")]),
            nonStreams: nonStreams);

        var result = await service.ProxyAsync(CreateResponsesContext(
            "shared-model",
            new Dictionary<string, string>
            {
                ["User-Agent"] = "Codex Desktop/0.140.0-alpha.2",
                ["x-oai-attestation"] = "attestation-token",
                ["thread-id"] = "thread-id"
            }));

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(nonStreams.LastContext);
        var headers = Assert.IsType<Dictionary<string, object?>>(nonStreams.LastContext!.Route.Channel["headers"]);
        Assert.Equal("configured-agent", headers["User-Agent"]);
        Assert.Equal("configured-attestation", headers["x-oai-attestation"]);
        Assert.Equal("thread-id", headers["thread-id"]);
    }

    private static ProxyEndpointService CreateService(
        IChannelCapacityService capacity,
        IProxyRouteService routes,
        IChannelCircuitBreakerService? breaker = null,
        IChannelAffinityService? affinity = null,
        IProxyNonStreamService? nonStreams = null,
        IProxyStreamService? streams = null)
    {
        return new ProxyEndpointService(
            new StubProxyLogService(),
            new StubProxyRequestService(),
            routes,
            capacity,
            breaker ?? new ChannelCircuitBreakerService(),
            affinity ?? new ChannelAffinityService(),
            new StubProxyImageFallbackService(),
            nonStreams ?? new StubProxyNonStreamService(_ =>
                Task.FromResult(new ProxyNonStreamResult(200, new { ok = true }))),
            streams ?? new StubProxyStreamService(_ => Task.CompletedTask));
    }

    private static ProxyEndpointContext CreateChatContext(string model)
    {
        return CreateChatContext(CreateChatPayload(model));
    }

    private static ProxyEndpointContext CreateChatContext(Dictionary<string, object?> payload)
    {
        return CreateChatContext(payload, new StubProxyStreamWriter());
    }

    private static ProxyEndpointContext CreateChatContext(
        Dictionary<string, object?> payload,
        IProxyStreamWriter streamWriter)
    {
        return new ProxyEndpointContext(
            ProtocolConverter.Chat,
            payload,
            "Bearer test",
            new ProxyRequestMetadata("POST", "/v1/chat/completions", null, new Dictionary<string, string>()),
            streamWriter,
            CancellationToken.None);
    }

    private static ProxyEndpointContext CreateResponsesContext(
        string model,
        IReadOnlyDictionary<string, string> headers)
    {
        return new ProxyEndpointContext(
            ProtocolConverter.Responses,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = model,
                ["input"] = "ping"
            },
            "Bearer test",
            new ProxyRequestMetadata("POST", "/v1/responses", null, headers),
            new StubProxyStreamWriter(),
            CancellationToken.None);
    }

    private static Dictionary<string, object?> CreateChatPayload(string model)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["messages"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = "ping"
                }
            }
        };
    }

    private static Dictionary<string, object?> CreateChatPayload(string model, string promptCacheKey)
    {
        var payload = CreateChatPayload(model);
        payload["prompt_cache_key"] = promptCacheKey;
        return payload;
    }

    private static Dictionary<string, object?> CreateChannel(
        string id,
        int priority,
        int? capacity = null)
    {
        var channel = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["name"] = id,
            ["type"] = ProtocolConverter.Chat,
            ["priority"] = priority,
            ["position"] = 0,
            ["compat"] = new Dictionary<string, object?>()
        };
        if (capacity.HasValue)
        {
            channel["capacity"] = capacity.Value;
        }

        return channel;
    }

    private static ProxyRouteDto CreateRoute(
        Dictionary<string, object?> channel,
        string model,
        string upstreamModel)
    {
        return new ProxyRouteDto(
            channel,
            model,
            upstreamModel,
            supportsImage: false,
            matchedModelMapping: true);
    }

    private sealed class StubProxyRouteService : IProxyRouteService
    {
        private readonly IReadOnlyList<ProxyRouteDto> _candidates;

        public StubProxyRouteService(IReadOnlyList<ProxyRouteDto> candidates)
        {
            _candidates = candidates;
        }

        public ProxyRouteDto ChooseRoute(string ownerUsername, string? model, bool requestContainsImages = false)
        {
            return _candidates[0];
        }

        public IReadOnlyList<ProxyRouteDto> ListRouteCandidates(
            string ownerUsername,
            string? model,
            bool requestContainsImages = false)
        {
            return _candidates;
        }

        public ProxyRouteDto? ChooseOcrRoute(string ownerUsername, string? model)
        {
            return null;
        }

        public IReadOnlyList<string> ListModels(string ownerUsername)
        {
            return [];
        }

        public IReadOnlyList<ProxyModelCapabilityDto> ListModelCapabilities(string ownerUsername)
        {
            return [];
        }
    }

    private sealed class StubProxyRequestService : IProxyRequestService
    {
        public ProxyRequestState StartRequest()
        {
            return new ProxyRequestState("req-1", "admin", 120);
        }

        public AuthenticatedAccessApiKeyDto AuthenticateAccessKey(string? authorizationHeader)
        {
            return new AuthenticatedAccessApiKeyDto(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "admin",
                "test",
                "sk-test",
                "suffix",
                "sk-***",
                true,
                0,
                0,
                null,
                new AccessApiKeyUserDto(Guid.NewGuid(), "admin", "superadmin", true));
        }
    }

    private sealed class StubProxyImageFallbackService : IProxyImageFallbackService
    {
        public Task<ProxyImageFallbackResult> RewriteAsync(ProxyImageFallbackContext context)
        {
            return Task.FromResult(new ProxyImageFallbackResult(context.Payload, usedOcr: false));
        }
    }

    private sealed class StubProxyNonStreamService : IProxyNonStreamService
    {
        private readonly Func<ProxyNonStreamContext, Task<ProxyNonStreamResult>> _handler;

        public StubProxyNonStreamService(Func<ProxyNonStreamContext, Task<ProxyNonStreamResult>> handler)
        {
            _handler = handler;
        }

        public ProxyNonStreamContext? LastContext { get; private set; }

        public Task<ProxyNonStreamResult> SendAsync(ProxyNonStreamContext context)
        {
            LastContext = context;
            return _handler(context);
        }
    }

    private sealed class StubProxyStreamService : IProxyStreamService
    {
        private readonly Func<ProxyStreamContext, Task> _handler;

        public StubProxyStreamService(Func<ProxyStreamContext, Task> handler)
        {
            _handler = handler;
        }

        public ProxyStreamContext? LastContext { get; private set; }

        public Task StreamAsync(ProxyStreamContext context)
        {
            LastContext = context;
            return _handler(context);
        }
    }

    private sealed class RecordingProxyStreamWriter : IProxyStreamWriter
    {
        public List<string> WrittenLines { get; } = [];

        public void PrepareSse()
        {
        }

        public async Task<StreamWriteMetrics> WriteLinesAsync(
            IAsyncEnumerable<string> lines,
            Func<string, bool> countsForTtft,
            Func<int> elapsedMilliseconds,
            CancellationToken cancellationToken = default)
        {
            await foreach (var line in lines.WithCancellation(cancellationToken))
            {
                WrittenLines.Add(line);
            }

            return new StreamWriteMetrics();
        }
    }

    private static async IAsyncEnumerable<string> ToAsyncEnumerable(params string[] lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }

    private sealed class StubProxyLogService : IProxyLogService
    {
        public Guid CreateQueuedLog(ProxyRequestLogQueuedContext context)
        {
            return Guid.NewGuid();
        }

        public void MarkProcessing(Guid requestLogId, ProxyRequestLogProcessingContext context)
        {
        }

        public void CompleteLog(Guid requestLogId, ProxyLogContext context, ProxyRequestMetadata request)
        {
        }

        public Guid WriteLog(ProxyLogContext context, ProxyRequestMetadata request)
        {
            return Guid.Empty;
        }

        public Guid WriteLog(ProxyRequestLogContext context)
        {
            return Guid.Empty;
        }
    }

    private sealed class StubProxyStreamWriter : IProxyStreamWriter
    {
        public void PrepareSse()
        {
        }

        public Task<StreamWriteMetrics> WriteLinesAsync(
            IAsyncEnumerable<string> lines,
            Func<string, bool> countsForTtft,
            Func<int> elapsedMilliseconds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new StreamWriteMetrics());
        }
    }
}

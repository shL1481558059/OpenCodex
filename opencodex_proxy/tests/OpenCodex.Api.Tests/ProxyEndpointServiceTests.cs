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

    private static ProxyEndpointService CreateService(
        IChannelCapacityService capacity,
        IProxyRouteService routes,
        IChannelAffinityService? affinity = null,
        IProxyNonStreamService? nonStreams = null,
        IProxyStreamService? streams = null)
    {
        return new ProxyEndpointService(
            new StubProxyLogService(),
            new StubProxyRequestService(),
            routes,
            capacity,
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
        return new ProxyEndpointContext(
            ProtocolConverter.Chat,
            payload,
            "Bearer test",
            new ProxyRequestMetadata("POST", "/v1/chat/completions", null, new Dictionary<string, string>()),
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
                1,
                "admin",
                "test",
                "sk-test",
                "suffix",
                "sk-***",
                true,
                0,
                0,
                null,
                new AccessApiKeyUserDto("admin", "superadmin", true));
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

    private sealed class StubProxyLogService : IProxyLogService
    {
        public long WriteLog(ProxyLogContext context, ProxyRequestMetadata request)
        {
            return 0;
        }

        public long WriteLog(ProxyRequestLogContext context)
        {
            return 0;
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

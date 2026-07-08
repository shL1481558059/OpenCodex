using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ChannelCircuitBreakerServiceTests
{
    [Fact]
    public async Task RecordFailure_ReachesThreshold_OpensCircuit()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 3,
            openDuration: TimeSpan.FromSeconds(30),
            halfOpenMaxProbeRequests: 1,
            redis: null,
            clock: () => now);

        await service.RecordFailureAsync("admin", "primary", new UpstreamException("1", ProxyHttpStatus.BadGateway));
        await service.RecordFailureAsync("admin", "primary", new UpstreamException("2", ProxyHttpStatus.BadGateway));
        await service.RecordFailureAsync("admin", "primary", new UpstreamException("3", ProxyHttpStatus.BadGateway));

        Assert.Equal(
            ChannelHealthStatus.Open,
            await service.GetHealthStatusAsync("admin", "primary", enabled: true));
    }

    [Fact]
    public async Task OpenCircuit_ExpiresToHalfOpen()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            redis: null,
            clock: () => now);

        await service.RecordFailureAsync("admin", "primary", new UpstreamException("boom", ProxyHttpStatus.BadGateway));
        now = now.AddSeconds(11);

        Assert.Equal(
            ChannelHealthStatus.HalfOpen,
            await service.GetHealthStatusAsync("admin", "primary", enabled: true));
    }

    [Fact]
    public async Task HalfOpen_Success_ClosesCircuit()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            redis: null,
            clock: () => now);

        await service.RecordFailureAsync("admin", "primary", new UpstreamException("boom", ProxyHttpStatus.BadGateway));
        now = now.AddSeconds(11);

        Assert.Equal(ChannelHealthStatus.HalfOpen, await service.GetHealthStatusAsync("admin", "primary", enabled: true));
        Assert.True(await service.TryAcquireHalfOpenProbeAsync("admin", "primary"));

        await service.RecordSuccessAsync("admin", "primary");

        Assert.Equal(ChannelHealthStatus.Healthy, await service.GetHealthStatusAsync("admin", "primary", enabled: true));
    }

    [Fact]
    public async Task HalfOpen_Failure_ReopensCircuit()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            redis: null,
            clock: () => now);

        await service.RecordFailureAsync("admin", "primary", new UpstreamException("boom", ProxyHttpStatus.BadGateway));
        now = now.AddSeconds(11);

        Assert.Equal(ChannelHealthStatus.HalfOpen, await service.GetHealthStatusAsync("admin", "primary", enabled: true));
        Assert.True(await service.TryAcquireHalfOpenProbeAsync("admin", "primary"));

        await service.RecordFailureAsync("admin", "primary", new UpstreamException("again", ProxyHttpStatus.BadGateway));

        Assert.Equal(ChannelHealthStatus.Open, await service.GetHealthStatusAsync("admin", "primary", enabled: true));
    }

    [Fact]
    public async Task GetHealthStatus_DisabledChannel_ReturnsDisabled()
    {
        var service = new ChannelCircuitBreakerService();

        Assert.Equal(ChannelHealthStatus.Disabled, await service.GetHealthStatusAsync("admin", "primary", enabled: false));
    }

    [Fact]
    public async Task RecordFailure_LocalBadRequest_DoesNotCount()
    {
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            redis: null,
            clock: () => DateTimeOffset.UtcNow);

        var counted = await service.RecordFailureAsync("admin", "primary", new BadRequestException("local bad request"));

        Assert.False(counted);
        Assert.Equal(ChannelHealthStatus.Healthy, await service.GetHealthStatusAsync("admin", "primary", enabled: true));
    }

    [Fact]
    public async Task RecordFailure_UpstreamForbidden_CountsAndOpensCircuit()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(30),
            halfOpenMaxProbeRequests: 1,
            redis: null,
            clock: () => now);

        var counted = await service.RecordFailureAsync(
            "admin",
            "primary",
            new UpstreamException("upstream returned HTTP 403", ProxyHttpStatus.Forbidden));

        Assert.True(counted);
        Assert.Equal(
            ChannelHealthStatus.Open,
            await service.GetHealthStatusAsync("admin", "primary", enabled: true));
    }

    [Fact]
    public async Task RecordFailure_UpstreamUnauthorized_DoesNotCount()
    {
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(30),
            halfOpenMaxProbeRequests: 1,
            redis: null,
            clock: () => DateTimeOffset.UtcNow);

        var counted = await service.RecordFailureAsync(
            "admin",
            "primary",
            new UpstreamException("upstream returned HTTP 401", ProxyHttpStatus.Unauthorized));

        Assert.False(counted);
        Assert.Equal(
            ChannelHealthStatus.Healthy,
            await service.GetHealthStatusAsync("admin", "primary", enabled: true));
    }

    [Fact]
    public async Task Reset_ClearsOpenCircuitBackToHealthy()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            redis: null,
            clock: () => now);

        await service.RecordFailureAsync("admin", "primary", new UpstreamException("boom", ProxyHttpStatus.BadGateway));

        Assert.Equal(ChannelHealthStatus.Open, await service.GetHealthStatusAsync("admin", "primary", enabled: true));

        await service.ResetAsync("admin", "primary");

        Assert.Equal(ChannelHealthStatus.Healthy, await service.GetHealthStatusAsync("admin", "primary", enabled: true));
    }

    [Fact]
    public async Task RecordFailure_ZeroDuration_DoesNotMarkCircuitOpen()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            redis: null,
            clock: () => now);

        var counted = await service.RecordFailureAsync(
            "admin",
            "primary",
            new UpstreamException("boom", ProxyHttpStatus.BadGateway),
            TimeSpan.Zero);

        Assert.True(counted);
        Assert.Equal(
            ChannelHealthStatus.Healthy,
            await service.GetHealthStatusAsync("admin", "primary", enabled: true, TimeSpan.Zero));
    }
}

using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ChannelCircuitBreakerServiceTests
{
    [Fact]
    public void RecordFailure_ReachesThreshold_OpensCircuit()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 3,
            openDuration: TimeSpan.FromSeconds(30),
            halfOpenMaxProbeRequests: 1,
            clock: () => now);

        service.RecordFailure("admin", "primary", new UpstreamException("1", ProxyHttpStatus.BadGateway));
        service.RecordFailure("admin", "primary", new UpstreamException("2", ProxyHttpStatus.BadGateway));
        service.RecordFailure("admin", "primary", new UpstreamException("3", ProxyHttpStatus.BadGateway));

        Assert.Equal(
            ChannelHealthStatus.Open,
            service.GetHealthStatus("admin", "primary", enabled: true));
    }

    [Fact]
    public void OpenCircuit_ExpiresToHalfOpen()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            clock: () => now);

        service.RecordFailure("admin", "primary", new UpstreamException("boom", ProxyHttpStatus.BadGateway));
        now = now.AddSeconds(11);

        Assert.Equal(
            ChannelHealthStatus.HalfOpen,
            service.GetHealthStatus("admin", "primary", enabled: true));
    }

    [Fact]
    public void HalfOpen_Success_ClosesCircuit()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            clock: () => now);

        service.RecordFailure("admin", "primary", new UpstreamException("boom", ProxyHttpStatus.BadGateway));
        now = now.AddSeconds(11);

        Assert.Equal(ChannelHealthStatus.HalfOpen, service.GetHealthStatus("admin", "primary", enabled: true));
        Assert.True(service.TryAcquireHalfOpenProbe("admin", "primary"));

        service.RecordSuccess("admin", "primary");

        Assert.Equal(ChannelHealthStatus.Healthy, service.GetHealthStatus("admin", "primary", enabled: true));
    }

    [Fact]
    public void HalfOpen_Failure_ReopensCircuit()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            clock: () => now);

        service.RecordFailure("admin", "primary", new UpstreamException("boom", ProxyHttpStatus.BadGateway));
        now = now.AddSeconds(11);

        Assert.Equal(ChannelHealthStatus.HalfOpen, service.GetHealthStatus("admin", "primary", enabled: true));
        Assert.True(service.TryAcquireHalfOpenProbe("admin", "primary"));

        service.RecordFailure("admin", "primary", new UpstreamException("again", ProxyHttpStatus.BadGateway));

        Assert.Equal(ChannelHealthStatus.Open, service.GetHealthStatus("admin", "primary", enabled: true));
    }

    [Fact]
    public void GetHealthStatus_DisabledChannel_ReturnsDisabled()
    {
        var service = new ChannelCircuitBreakerService();

        Assert.Equal(ChannelHealthStatus.Disabled, service.GetHealthStatus("admin", "primary", enabled: false));
    }

    [Fact]
    public void RecordFailure_LocalBadRequest_DoesNotCount()
    {
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            clock: () => DateTimeOffset.UtcNow);

        var counted = service.RecordFailure("admin", "primary", new BadRequestException("local bad request"));

        Assert.False(counted);
        Assert.Equal(ChannelHealthStatus.Healthy, service.GetHealthStatus("admin", "primary", enabled: true));
    }

    [Fact]
    public void RecordFailure_UpstreamForbidden_CountsAndOpensCircuit()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(30),
            halfOpenMaxProbeRequests: 1,
            clock: () => now);

        var counted = service.RecordFailure(
            "admin",
            "primary",
            new UpstreamException("upstream returned HTTP 403", ProxyHttpStatus.Forbidden));

        Assert.True(counted);
        Assert.Equal(
            ChannelHealthStatus.Open,
            service.GetHealthStatus("admin", "primary", enabled: true));
    }

    [Fact]
    public void RecordFailure_UpstreamUnauthorized_DoesNotCount()
    {
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(30),
            halfOpenMaxProbeRequests: 1,
            clock: () => DateTimeOffset.UtcNow);

        var counted = service.RecordFailure(
            "admin",
            "primary",
            new UpstreamException("upstream returned HTTP 401", ProxyHttpStatus.Unauthorized));

        Assert.False(counted);
        Assert.Equal(
            ChannelHealthStatus.Healthy,
            service.GetHealthStatus("admin", "primary", enabled: true));
    }

    [Fact]
    public void Reset_ClearsOpenCircuitBackToHealthy()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelCircuitBreakerService(
            failureThreshold: 1,
            openDuration: TimeSpan.FromSeconds(10),
            halfOpenMaxProbeRequests: 1,
            clock: () => now);

        service.RecordFailure("admin", "primary", new UpstreamException("boom", ProxyHttpStatus.BadGateway));

        Assert.Equal(ChannelHealthStatus.Open, service.GetHealthStatus("admin", "primary", enabled: true));

        service.Reset("admin", "primary");

        Assert.Equal(ChannelHealthStatus.Healthy, service.GetHealthStatus("admin", "primary", enabled: true));
    }
}

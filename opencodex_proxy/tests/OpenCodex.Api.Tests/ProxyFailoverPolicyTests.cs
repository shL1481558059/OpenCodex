using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyFailoverPolicyTests
{
    [Theory]
    [InlineData(ProxyHttpStatus.Forbidden)]
    [InlineData(ProxyHttpStatus.BadRequest)]
    [InlineData(ProxyHttpStatus.TooManyRequests)]
    [InlineData(ProxyHttpStatus.InternalServerError)]
    [InlineData(ProxyHttpStatus.BadGateway)]
    [InlineData(ProxyHttpStatus.ServiceUnavailable)]
    [InlineData(ProxyHttpStatus.GatewayTimeout)]
    public void CanFailover_UpstreamTransientOrForbiddenStatus_ReturnsTrue(int statusCode)
    {
        var exception = new UpstreamException("upstream failed", statusCode);

        Assert.True(ProxyFailoverPolicy.CanFailover(exception));
    }

    [Fact]
    public void CanFailover_UpstreamUnauthorized_DoesNotFailover()
    {
        var exception = new UpstreamException("auth required", ProxyHttpStatus.Unauthorized);

        Assert.False(ProxyFailoverPolicy.CanFailover(exception));
    }

    [Fact]
    public void CanFailover_LocalBadRequest_DoesNotFailover()
    {
        var exception = new BadRequestException("local bad request");

        Assert.False(ProxyFailoverPolicy.CanFailover(exception));
    }

    [Fact]
    public void CanFailover_RoutingException_DoesNotFailover()
    {
        var exception = new RoutingException("no route");

        Assert.False(ProxyFailoverPolicy.CanFailover(exception));
    }

    [Fact]
    public void CanFailover_NonProxyException_DoesNotFailover()
    {
        var exception = new InvalidOperationException("unrelated");

        Assert.False(ProxyFailoverPolicy.CanFailover(exception));
    }
}

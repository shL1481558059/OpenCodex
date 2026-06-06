using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Infrastructure;

namespace OpenCodex.Api.Tests.Infrastructure;

public sealed class ProxyRequestMetadataFactoryTests
{
    [Fact]
    public void FromHttpRequestBuildsMetadataAndRedactsAuthorization()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/v1/responses";
        context.Request.Headers.Authorization = "Bearer abcdefghijklmnopqrstuvwxyz";
        context.Request.Headers["X-Test"] = "yes";

        var metadata = ProxyRequestMetadataFactory.FromHttpRequest(
            context.Request,
            "203.0.113.10");

        Assert.Equal("POST", metadata.Method);
        Assert.Equal("/v1/responses", metadata.Path);
        Assert.Equal("203.0.113.10", metadata.ClientIp);
        Assert.Equal("Bearer a...wxyz", metadata.Headers["Authorization"]);
        Assert.Equal("yes", metadata.Headers["X-Test"]);
    }

    [Fact]
    public void FromHttpRequestRedactsShortAuthorization()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "short-token";

        var metadata = ProxyRequestMetadataFactory.FromHttpRequest(
            context.Request,
            clientIp: null);

        Assert.Equal("...", metadata.Headers["Authorization"]);
    }
}

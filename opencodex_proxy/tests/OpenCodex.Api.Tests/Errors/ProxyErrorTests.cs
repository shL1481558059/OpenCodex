using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCodex.Api.Errors;

namespace OpenCodex.Api.Tests.Errors;

public sealed class ProxyErrorTests
{
    [Fact]
    public async Task ProxyExceptionReturnsPythonCompatibleErrorShape()
    {
        var response = await InvokeMiddlewareAsync(new ProxyException("unexpected"));

        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);

        using var document = JsonDocument.Parse(response.Body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("unexpected", error.GetProperty("message").GetString());
        Assert.Equal("proxy_error", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task BadRequestExceptionReturnsBadRequestTypeAndStatus()
    {
        var response = await InvokeMiddlewareAsync(new BadRequestException("request body must be a JSON object"));

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(response.Body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("request body must be a JSON object", error.GetProperty("message").GetString());
        Assert.Equal("bad_request", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task BadRequestExceptionCanOverrideStatusCodeLikePythonProxyError()
    {
        var response = await InvokeMiddlewareAsync(new BadRequestException(
            "valid bearer api key required",
            StatusCodes.Status401Unauthorized));

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);

        using var document = JsonDocument.Parse(response.Body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("valid bearer api key required", error.GetProperty("message").GetString());
        Assert.Equal("bad_request", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task RoutingExceptionReturnsRoutingErrorType()
    {
        var response = await InvokeMiddlewareAsync(new RoutingException("no enabled channels configured"));

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(response.Body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("no enabled channels configured", error.GetProperty("message").GetString());
        Assert.Equal("routing_error", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task UpstreamExceptionIncludesOptionalChannelAndBody()
    {
        var upstreamBody = new Dictionary<string, object?>
        {
            ["error"] = "bad gateway"
        };

        var response = await InvokeMiddlewareAsync(new UpstreamException(
            "upstream returned HTTP 502",
            StatusCodes.Status502BadGateway,
            upstreamBody,
            "chat"));

        Assert.Equal(StatusCodes.Status502BadGateway, response.StatusCode);

        using var document = JsonDocument.Parse(response.Body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("upstream returned HTTP 502", error.GetProperty("message").GetString());
        Assert.Equal("upstream_error", error.GetProperty("type").GetString());
        Assert.Equal("chat", error.GetProperty("channel_id").GetString());
        Assert.Equal("bad gateway", error.GetProperty("upstream").GetProperty("error").GetString());
    }

    [Fact]
    public async Task NonProxyExceptionsReturnStableApiResultWithTraceId()
    {
        var response = await InvokeMiddlewareAsync(new InvalidOperationException("boom"));

        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);

        using var document = JsonDocument.Parse(response.Body);
        Assert.False(document.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal(500000, document.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("An unexpected error occurred.", document.RootElement.GetProperty("message").GetString());
        Assert.Equal("trace-test", document.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task RequestTraceMiddlewareUsesIncomingRequestId()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Request-Id"] = "client-trace";
        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new OpenCodex.Api.Infrastructure.RequestTraceMiddleware(next);

        await middleware.InvokeAsync(context);
        await context.Response.StartAsync();

        Assert.Equal("client-trace", context.TraceIdentifier);
        Assert.Equal("client-trace", context.Response.Headers["X-Request-Id"]);
    }

    private static async Task<MiddlewareResponse> InvokeMiddlewareAsync(
        Exception exception,
        string traceId = "trace-test")
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.TraceIdentifier = traceId;
        context.Response.Body = body;

        RequestDelegate next = _ => throw exception;
        var middleware = new ProxyErrorMiddleware(next, NullLogger<ProxyErrorMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        body.Position = 0;
        using var reader = new StreamReader(body);
        var responseBody = await reader.ReadToEndAsync();

        return new MiddlewareResponse(
            context.Response.StatusCode,
            context.Response.ContentType,
            responseBody);
    }

    private sealed record MiddlewareResponse(int StatusCode, string? ContentType, string Body);
}

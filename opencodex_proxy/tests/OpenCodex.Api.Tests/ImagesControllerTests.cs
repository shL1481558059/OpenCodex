using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Controllers;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ImagesControllerTests
{
    [Fact]
    public void Routes_ExposeVersionedAndUnversionedGenerationAndEditEndpoints()
    {
        Assert.Equal(
            ["/images/generations", "/v1/images/generations"],
            Routes(nameof(ImagesController.Generations)));
        Assert.Equal(
            ["/images/edits", "/v1/images/edits"],
            Routes(nameof(ImagesController.Edits)));
    }

    [Fact]
    public async Task Generations_RejectsWrongContentTypeAndStream()
    {
        var controller = CreateController(new StubBodyReader(new Dictionary<string, object?> { ["stream"] = true }));
        controller.Request.ContentType = "text/plain";
        var contentTypeError = await Assert.ThrowsAsync<BadRequestException>(() => controller.Generations());
        Assert.Equal(415, contentTypeError.StatusCode);

        controller.Request.ContentType = "application/json; charset=utf-8";
        var streamError = await Assert.ThrowsAsync<BadRequestException>(() => controller.Generations());
        Assert.Equal(400, streamError.StatusCode);
    }

    [Fact]
    public async Task Edits_DoesNotReadFormWhenAuthenticationFails()
    {
        var reader = new CountingEditReader();
        var controller = CreateController(new StubBodyReader(null), new RejectingRequestService(), reader);

        await Assert.ThrowsAsync<BadRequestException>(() => controller.Edits());

        Assert.Equal(0, reader.ReadCount);
    }

    private static ImagesController CreateController(
        IRequestBodyReader bodyReader,
        IProxyRequestService? requests = null,
        IImageEditRequestReader? editReader = null)
    {
        var controller = new ImagesController(
            bodyReader,
            new StubImagesService(),
            requests ?? new RejectingRequestService(),
            editReader ?? new CountingEditReader());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static string[] Routes(string methodName) => typeof(ImagesController)
        .GetMethod(methodName)!
        .GetCustomAttributes(typeof(HttpPostAttribute), false)
        .Cast<HttpPostAttribute>()
        .Select(attribute => attribute.Template!)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

    private sealed class StubBodyReader(Dictionary<string, object?>? payload) : IRequestBodyReader
    {
        public Task<Dictionary<string, object?>?> ReadJsonObjectAsync(HttpRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(payload);
    }

    private sealed class CountingEditReader : IImageEditRequestReader
    {
        public int ReadCount { get; private set; }
        public Task<ImageEditRequest> ReadAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new NotSupportedException();
        }
    }

    private sealed class RejectingRequestService : IProxyRequestService
    {
        public ProxyRequestState StartRequest() => throw new NotSupportedException();
        public Task<AuthenticatedAccessApiKeyDto> AuthenticateAccessKeyAsync(string? authorizationHeader)
            => throw new BadRequestException("unauthorized", 401);
    }

    private sealed class StubImagesService : IProxyImagesEndpointService
    {
        public Task<ProxyEndpointResult> GenerateAsync(ImageGenerationContext context) => throw new NotSupportedException();
        public Task<ProxyEndpointResult> EditAsync(ImageEditContext context) => throw new NotSupportedException();
    }
}

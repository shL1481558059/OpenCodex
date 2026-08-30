using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Api.Services;

/// <summary>
/// 图片生成 / 图片编辑代理端点实现：读取请求、鉴权并转发给上游图片端点服务。
/// </summary>
public sealed class ImagesProxyService : IImagesProxyService
{
    private readonly IRequestBodyReader _bodyReader;
    private readonly IProxyImagesEndpointService _images;
    private readonly IProxyRequestService _requests;
    private readonly IImageEditRequestService _editReader;

    public ImagesProxyService(
        IRequestBodyReader bodyReader,
        IProxyImagesEndpointService images,
        IProxyRequestService requests,
        IImageEditRequestService editReader)
    {
        _bodyReader = bodyReader;
        _images = images;
        _requests = requests;
        _editReader = editReader;
    }

    public async Task<IActionResult> GenerationsAsync(
        HttpRequest request,
        HttpResponse response)
    {
        if (!IsJsonContentType(request.ContentType))
        {
            throw new BadRequestException(
                "图片生成只接受 application/json",
                StatusCodes.Status415UnsupportedMediaType);
        }

        var payload = await _bodyReader.ReadJsonObjectAsync(
            request,
            request.HttpContext.RequestAborted);
        if (payload is null)
        {
            throw new BadRequestException("请求体必须是 JSON 对象");
        }

        if (IsStreamRequested(payload))
        {
            throw new BadRequestException("图片接口首版不支持 stream=true");
        }

        var result = await _images.GenerateAsync(new ImageGenerationContext(
            new ImageGenerationRequest(new ImageProxyParameters(payload)),
            AuthorizationHeader(request),
            RequestMetadata(request),
            new ProxyResponseBodyWriter(response),
            request.HttpContext.RequestAborted));
        return result.IsEmpty
            ? new EmptyResult()
            : StatusCodeResult(response, result.StatusCode, result.Payload);
    }

    public async Task<IActionResult> EditsAsync(
        HttpRequest request,
        HttpResponse response)
    {
        var authorization = AuthorizationHeader(request);
        await _requests.AuthenticateAccessKeyAsync(authorization);
        var editRequest = await _editReader.ReadAsync(
            request,
            request.HttpContext.RequestAborted);
        var result = await _images.EditAsync(new ImageEditContext(
            editRequest,
            authorization,
            RequestMetadata(request),
            new ProxyResponseBodyWriter(response),
            request.HttpContext.RequestAborted));
        return result.IsEmpty
            ? new EmptyResult()
            : StatusCodeResult(response, result.StatusCode, result.Payload);
    }

    private static ProxyRequestMetadata RequestMetadata(HttpRequest request)
        => ProxyRequestMetadataFactory.FromHttpRequest(
            request,
            request.HttpContext.Connection.RemoteIpAddress?.ToString());

    private static string? AuthorizationHeader(HttpRequest request)
        => request.Headers.TryGetValue(HeaderNames.Authorization, out var values)
            ? values.ToString()
            : null;

    private static IActionResult StatusCodeResult(
        HttpResponse response,
        int statusCode,
        object? value)
        => new ObjectResult(value) { StatusCode = statusCode };

    private static bool IsJsonContentType(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    private static bool IsStreamRequested(IReadOnlyDictionary<string, object?> payload)
        => payload.TryGetValue("stream", out var stream)
            && (stream is true || stream is string text && bool.TryParse(text, out var parsed) && parsed);
}

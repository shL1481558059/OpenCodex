using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using OpenCodex.Api.Infrastructure;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.Core.Errors;

namespace OpenCodex.Api.Controllers;

public sealed class ImagesController : ApiControllerBase
{
    private readonly IRequestBodyReader _bodyReader;
    private readonly IProxyImagesEndpointService _images;
    private readonly IProxyRequestService _requests;
    private readonly IImageEditRequestReader _editReader;

    public ImagesController(
        IRequestBodyReader bodyReader,
        IProxyImagesEndpointService images,
        IProxyRequestService requests,
        IImageEditRequestReader editReader)
    {
        _bodyReader = bodyReader;
        _images = images;
        _requests = requests;
        _editReader = editReader;
    }

    [HttpPost("/images/generations")]
    [HttpPost("/v1/images/generations")]
    public async Task<IActionResult> Generations()
    {
        if (!IsJsonContentType(Request.ContentType))
        {
            throw new BadRequestException("图片生成只接受 application/json", StatusCodes.Status415UnsupportedMediaType);
        }

        var payload = await _bodyReader.ReadJsonObjectAsync(Request, HttpContext.RequestAborted);
        if (payload is null) throw new BadRequestException("请求体必须是 JSON 对象");
        if (IsStreamRequested(payload)) throw new BadRequestException("图片接口首版不支持 stream=true");

        var result = await _images.GenerateAsync(new ImageGenerationContext(
            new ImageGenerationRequest(new ImageProxyParameters(payload)),
            AuthorizationHeader(),
            RequestMetadata(),
            new ProxyResponseBodyWriter(Response),
            HttpContext.RequestAborted));
        return result.IsEmpty ? new EmptyResult() : StatusCode(result.StatusCode, result.Payload);
    }

    [HttpPost("/images/edits")]
    [HttpPost("/v1/images/edits")]
    public async Task<IActionResult> Edits()
    {
        var authorization = AuthorizationHeader();
        await _requests.AuthenticateAccessKeyAsync(authorization);
        var request = await _editReader.ReadAsync(Request, HttpContext.RequestAborted);
        var result = await _images.EditAsync(new ImageEditContext(
            request,
            authorization,
            RequestMetadata(),
            new ProxyResponseBodyWriter(Response),
            HttpContext.RequestAborted));
        return result.IsEmpty ? new EmptyResult() : StatusCode(result.StatusCode, result.Payload);
    }

    private ProxyRequestMetadata RequestMetadata()
        => ProxyRequestMetadataFactory.FromHttpRequest(Request, HttpContext.Connection.RemoteIpAddress?.ToString());

    private string? AuthorizationHeader()
        => Request.Headers.TryGetValue(HeaderNames.Authorization, out var values) ? values.ToString() : null;

    private static bool IsJsonContentType(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    private static bool IsStreamRequested(IReadOnlyDictionary<string, object?> payload)
        => payload.TryGetValue("stream", out var stream)
            && (stream is true || stream is string text && bool.TryParse(text, out var parsed) && parsed);
}

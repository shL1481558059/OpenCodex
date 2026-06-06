using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Protocols;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ProxyController : ApiControllerBase
{
    private readonly IRequestBodyReader _bodyReader;
    private readonly IProxyEndpointService _proxy;

    public ProxyController(
        IRequestBodyReader bodyReader,
        IProxyEndpointService proxy)
    {
        _bodyReader = bodyReader;
        _proxy = proxy;
    }

    [HttpPost("/v1/responses")]
    public Task<IActionResult> Responses()
    {
        return Proxy(ProtocolConverter.Responses);
    }

    [HttpPost("/v1/chat/completions")]
    public Task<IActionResult> ChatCompletions()
    {
        return Proxy(ProtocolConverter.Chat);
    }

    [HttpPost("/v1/messages")]
    public Task<IActionResult> Messages()
    {
        return Proxy(ProtocolConverter.Messages);
    }

    private async Task<IActionResult> Proxy(string entryProtocol)
    {
        var payload = await _bodyReader.ReadJsonObjectAsync(Request, HttpContext.RequestAborted);
        var result = await _proxy.ProxyAsync(
            new ProxyEndpointContext(
                entryProtocol,
                payload,
                AuthorizationHeader(),
                ProxyRequestMetadataFactory.FromHttpRequest(
                    Request,
                    HttpContext.Connection.RemoteIpAddress?.ToString()),
                new ProxyStreamResponseWriter(Response),
                HttpContext.RequestAborted));
        if (result.IsEmpty)
        {
            return new EmptyResult();
        }

        return StatusCode(result.StatusCode, result.Payload);
    }

    private string? AuthorizationHeader()
    {
        return Request.Headers.TryGetValue("Authorization", out var values)
            ? values.ToString()
            : null;
    }
}

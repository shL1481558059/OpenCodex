using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Services;
using OpenCodex.Core.Protocols;

namespace OpenCodex.Api.Controllers;

public sealed class ProxyController : ApiControllerBase
{
    private readonly IProxyService _proxyService;

    public ProxyController(
        IProxyService proxyService)
    {
        _proxyService = proxyService;
    }

    [HttpGet("/models")]
    [HttpGet("/v1/models")]
    public Task<IActionResult> Models()
    {
        return _proxyService.ModelsAsync(Request, Response);
    }

    [HttpPost("/responses")]
    [HttpPost("/v1/responses")]
    public Task<IActionResult> Responses()
    {
        return _proxyService.ProxyAsync(ProtocolConverter.Responses, Request, Response);
    }

    [HttpPost("/chat/completions")]
    [HttpPost("/v1/chat/completions")]
    public Task<IActionResult> ChatCompletions()
    {
        return _proxyService.ProxyAsync(ProtocolConverter.Chat, Request, Response);
    }

    [HttpPost("/messages")]
    [HttpPost("/v1/messages")]
    public Task<IActionResult> Messages()
    {
        return _proxyService.ProxyAsync(ProtocolConverter.Messages, Request, Response);
    }
}

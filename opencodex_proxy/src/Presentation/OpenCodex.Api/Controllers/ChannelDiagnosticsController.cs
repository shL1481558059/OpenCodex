using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.CoreBase.DTOs.ChannelDiagnostics;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ChannelDiagnosticsController : AuthenticatedApiControllerBase
{
    private readonly IChannelDiagnosticsService _channelDiagnostics;

    public ChannelDiagnosticsController(
        IWorkContext workContext,
        IChannelDiagnosticsService channelDiagnostics)
        : base(workContext)
    {
        _channelDiagnostics = channelDiagnostics;
    }

    [HttpPost("/channels/discover-models")]
    [HttpPost("/discover-models")]
    public async Task<IActionResult> DiscoverModels(ChannelDiagnosticsRequest request)
    {
        RequireUser();
        var result = await _channelDiagnostics.DiscoverModelsAsync(
            request.ToDictionary(),
            HttpContext.RequestAborted);
        return Api(result);
    }

    [HttpPost("/channels/test")]
    [HttpPost("/test-channel")]
    public async Task<IActionResult> TestChannel(ChannelDiagnosticsRequest request)
    {
        var user = RequireUser();
        var result = await _channelDiagnostics.TestChannelAsync(
            request.ToDictionary(),
            user,
            ProxyRequestMetadataFactory.FromHttpRequest(
                Request,
                HttpContext.Connection.RemoteIpAddress?.ToString()),
            HttpContext.RequestAborted);
        return Api(result);
    }

    [HttpPost("/channels/test/stream")]
    [HttpPost("/test-channel/stream")]
    public async Task TestChannelStream(ChannelDiagnosticsRequest request)
    {
        var user = RequireUser();
        await _channelDiagnostics.StreamTestChannelAsync(
            request.ToDictionary(),
            user,
            ProxyRequestMetadataFactory.FromHttpRequest(
                Request,
                HttpContext.Connection.RemoteIpAddress?.ToString()),
            new ProxyStreamResponseWriter(Response),
            HttpContext.RequestAborted);
    }
}

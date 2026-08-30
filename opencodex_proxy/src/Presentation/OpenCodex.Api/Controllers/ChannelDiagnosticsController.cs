using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.DTOs.ChannelDiagnostics;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ChannelDiagnosticsController : AuthenticatedApiControllerBase
{
    private readonly IChannelDiagnosticsControllerService _channelDiagnostics;

    public ChannelDiagnosticsController(
        IWorkContext workContext,
        IChannelDiagnosticsControllerService channelDiagnostics)
        : base(workContext)
    {
        _channelDiagnostics = channelDiagnostics;
    }

    [HttpPost("/channels/discover-models")]
    [HttpPost("/discover-models")]
    public async Task<IActionResult> DiscoverModels(ChannelDiscoverRequest request)
    {
        RequireUser();
        var result = await _channelDiagnostics.DiscoverModelsAsync(request, HttpContext.RequestAborted);
        return Api(result);
    }

    [HttpPost("/channels/test/stream")]
    [HttpPost("/test-channel/stream")]
    public async Task TestChannelStream(ChannelTestRequest request)
    {
        var user = RequireUser();
        await _channelDiagnostics.StreamTestChannelAsync(
            request,
            user,
            Request,
            Response,
            HttpContext.RequestAborted);
    }
}

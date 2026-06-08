using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.AdminChannelDiagnostics;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Api.Controllers;

public sealed class AdminChannelDiagnosticsController : AdminApiControllerBase
{
    private readonly IAdminChannelDiagnosticsService _channelDiagnostics;

    public AdminChannelDiagnosticsController(
        IAdminSessionService adminSession,
        IAdminChannelDiagnosticsService channelDiagnostics)
        : base(adminSession)
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
        RequireUser();
        var result = await _channelDiagnostics.TestChannelAsync(
            request.ToDictionary(),
            HttpContext.RequestAborted);
        return Api(result);
    }
}

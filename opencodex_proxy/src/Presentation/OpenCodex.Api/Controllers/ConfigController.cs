using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.Config;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ConfigController : AuthenticatedApiControllerBase
{
    private readonly IConfigService _config;

    public ConfigController(
        IWorkContext workContext,
        IConfigService config)
        : base(workContext)
    {
        _config = config;
    }

    [HttpGet("/config")]
    public IActionResult Config()
    {
        RequireUser();
        var result = _config.ReadConfig();
        return Api(result);
    }

    [HttpPost("/channels")]
    public IActionResult CreateChannel(ChannelRequest request)
    {
        RequireUser();
        var result = _config.CreateChannel(request);
        return Api(result);
    }

    [HttpPut("/channels/{channelId:guid}")]
    public IActionResult UpdateChannel(Guid channelId, ChannelRequest request)
    {
        RequireUser();
        var result = _config.UpdateChannel(channelId, request);
        return Api(result);
    }

    [HttpDelete("/channels/{channelId:guid}")]
    public IActionResult DeleteChannel(Guid channelId)
    {
        RequireUser();
        var result = _config.DeleteChannel(channelId);
        return Api(result);
    }

    [HttpPost("/config/import")]
    public IActionResult ImportConfig(ConfigSaveRequest request)
    {
        RequireUser();
        var result = _config.ImportConfig(request.ToDictionary());
        return Api(result);
    }

    [HttpPost("/channels/{channelId:guid}/reset-health")]
    public IActionResult ResetChannelHealth(Guid channelId)
    {
        RequireUser();
        var result = _config.ResetChannelHealth(channelId);
        return Api(result);
    }
}

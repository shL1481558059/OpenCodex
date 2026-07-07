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
    public async Task<IActionResult> CreateChannel(ChannelRequest request)
    {
        RequireUser();
        var result = await _config.CreateChannelAsync(request);
        return Api(result);
    }

    [HttpPut("/channels/{channelId:guid}")]
    public async Task<IActionResult> UpdateChannel(Guid channelId, ChannelRequest request)
    {
        RequireUser();
        var result = await _config.UpdateChannelAsync(channelId, request);
        return Api(result);
    }

    [HttpPatch("/channels/batch")]
    public async Task<IActionResult> BatchUpdateChannels(ChannelBatchUpdateRequest request)
    {
        RequireUser();
        var result = await _config.BatchUpdateChannelsAsync(request);
        return Api(result);
    }

    [HttpDelete("/channels/{channelId:guid}")]
    public async Task<IActionResult> DeleteChannel(Guid channelId)
    {
        RequireUser();
        var result = await _config.DeleteChannelAsync(channelId);
        return Api(result);
    }

    [HttpPost("/config/import")]
    public async Task<IActionResult> ImportConfig(ConfigSaveRequest request)
    {
        RequireUser();
        var result = await _config.ImportConfigAsync(request.ToDictionary());
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

using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.Channels;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ChannelController : AuthenticatedApiControllerBase
{
    private readonly IChannelService _channels;
    private readonly IChannelControllerService _channelRuntime;

    public ChannelController(
        IWorkContext workContext,
        IChannelService channels,
        IChannelControllerService channelRuntime)
        : base(workContext)
    {
        _channels = channels;
        _channelRuntime = channelRuntime;
    }

    [HttpGet("/channels")]
    public IActionResult Channels()
    {
        RequireUser();
        var result = _channels.ReadChannels();
        return Api(result);
    }

    [HttpGet("/channels/{channelId:guid}")]
    public IActionResult Channel(Guid channelId)
    {
        RequireUser();
        var result = _channels.ReadChannelById(channelId);
        return Api(result);
    }

    [HttpGet("/channels/runtime")]
    public IActionResult ChannelRuntime([FromQuery] string? ids)
    {
        RequireUser();
        var result = _channelRuntime.ReadChannelRuntime(ids);
        return Api(result);
    }

    [HttpPost("/channels")]
    public async Task<IActionResult> CreateChannel(ChannelRequest request)
    {
        RequireUser();
        var result = await _channels.CreateChannelAsync(request);
        return Api(result);
    }

    [HttpPut("/channels/{channelId:guid}")]
    public async Task<IActionResult> UpdateChannel(Guid channelId, ChannelRequest request)
    {
        RequireUser();
        var result = await _channels.UpdateChannelAsync(channelId, request);
        return Api(result);
    }

    [HttpPatch("/channels")]
    [HttpPatch("/channels/batch")]
    public async Task<IActionResult> BatchUpdateChannels(ChannelBatchUpdateRequest request)
    {
        RequireUser();
        var result = await _channels.BatchUpdateChannelsAsync(request);
        return Api(result);
    }

    [HttpDelete("/channels/{channelId:guid}")]
    public async Task<IActionResult> DeleteChannel(Guid channelId)
    {
        RequireUser();
        var result = await _channels.DeleteChannelAsync(channelId);
        return Api(result);
    }

    [HttpPost("/channels/bulk-import")]
    public async Task<IActionResult> ImportChannels(ChannelSaveRequest request)
    {
        RequireUser();
        var result = await _channels.ImportChannelsAsync(request.ToDictionary());
        return Api(result);
    }

    [HttpPost("/channels/{channelId:guid}/health-reset")]
    [HttpPost("/channels/{channelId:guid}/reset-health")]
    public async Task<IActionResult> ResetChannelHealth(Guid channelId)
    {
        RequireUser();
        var result = await _channels.ResetChannelHealthAsync(channelId);
        return Api(result);
    }

}

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

    [HttpGet("/channels")]
    public IActionResult Channels()
    {
        RequireUser();
        var result = _config.ReadConfig();
        return Api(result);
    }

    [HttpGet("/channels/{channelId:guid}")]
    public IActionResult Channel(Guid channelId)
    {
        RequireUser();
        var result = _config.ReadChannelById(channelId);
        return Api(result);
    }

    [HttpGet("/channels/runtime")]
    public IActionResult ChannelRuntime([FromQuery] string? ids)
    {
        RequireUser();
        IReadOnlyList<Guid>? idList = null;
        if (!string.IsNullOrWhiteSpace(ids))
        {
            // 宽松解析: 无法解析为 Guid 的值会被静默忽略, 不返回错误。
            idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                .Where(guid => guid != Guid.Empty)
                .ToList();
            if (idList.Count == 0)
            {
                idList = null;
            }
        }
        var result = _config.ReadChannelRuntime(idList);
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

    [HttpPatch("/channels")]
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

    [HttpPost("/channels/bulk-import")]
    [HttpPost("/config/import")]
    public async Task<IActionResult> ImportConfig(ConfigSaveRequest request)
    {
        RequireUser();
        var result = await _config.ImportConfigAsync(request.ToDictionary());
        return Api(result);
    }

    [HttpPost("/channels/{channelId:guid}/health-reset")]
    [HttpPost("/channels/{channelId:guid}/reset-health")]
    public async Task<IActionResult> ResetChannelHealth(Guid channelId)
    {
        RequireUser();
        var result = await _config.ResetChannelHealthAsync(channelId);
        return Api(result);
    }
}

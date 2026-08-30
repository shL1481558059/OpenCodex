using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Services;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

/// <summary>
/// 管理台实时状态推送：渠道容量、处理队列、近期错误、日志更新。
/// </summary>
public sealed class RealtimeStreamController : AuthenticatedApiControllerBase
{
    private readonly IRealtimeStreamService _streamService;

    public RealtimeStreamController(
        IWorkContext workContext,
        IRealtimeStreamService streamService)
        : base(workContext)
    {
        _streamService = streamService;
    }

    [HttpGet("/channels/runtime/stream")]
    public async Task ChannelRuntimeStream()
    {
        RequireUser();
        await _streamService.StreamChannelRuntimeAsync(Response, HttpContext.RequestAborted);
    }

    [HttpGet("/monitor/active-channels/stream")]
    public async Task ActiveChannelsStream()
    {
        RequireUser();
        await _streamService.StreamActiveChannelsAsync(Response, HttpContext.RequestAborted);
    }

    [HttpGet("/monitor/recent-errors/stream")]
    public async Task RecentErrorsStream()
    {
        RequireUser();
        await _streamService.StreamRecentErrorsAsync(Response, HttpContext.RequestAborted);
    }

    [HttpGet("/logs/stream")]
    public async Task LogsStream()
    {
        RequireUser();
        await _streamService.StreamLogsAsync(Response, HttpContext.RequestAborted);
    }
}

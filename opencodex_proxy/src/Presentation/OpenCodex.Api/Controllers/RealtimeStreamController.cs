using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenCodex.Api.Infrastructure;
using OpenCodex.CoreBase.DTOs.Config;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Events;
using OpenCodex.CoreBase.Services;
using Microsoft.AspNetCore.Mvc;

namespace OpenCodex.Api.Controllers;

/// <summary>
/// 管理台实时状态推送：渠道容量、处理队列、近期错误、日志更新。
/// 不注入 scoped DbContext 依赖，每帧快照通过 <see cref="IServiceScopeFactory"/> 临时解析服务。
/// </summary>
public sealed class RealtimeStreamController : AuthenticatedApiControllerBase
{
    private readonly IEventBus _eventBus;
    private readonly IServiceScopeFactory _scopeFactory;

    public RealtimeStreamController(
        IWorkContext workContext,
        IEventBus eventBus,
        IServiceScopeFactory scopeFactory)
        : base(workContext)
    {
        _eventBus = eventBus;
        _scopeFactory = scopeFactory;
    }

    [HttpGet("/channels/runtime/stream")]
    public async Task ChannelRuntimeStream()
    {
        RequireUser();
        await WriteChannelRuntimeStream();
    }

    [HttpGet("/monitor/active-channels/stream")]
    public async Task ActiveChannelsStream()
    {
        RequireUser();
        await WriteActiveChannelStream();
    }

    [HttpGet("/monitor/recent-errors/stream")]
    public async Task RecentErrorsStream()
    {
        RequireUser();
        await WriteRecentErrorsStream();
    }

    [HttpGet("/logs/stream")]
    public async Task LogsStream()
    {
        RequireUser();
        await WriteLogsStream();
    }

    private async Task WriteChannelRuntimeStream()
    {
        var user = RequireUser();
        var isSuperadmin = user.Role == "superadmin";
        var username = user.Username;

        ProxyStreamResponseWriter.PrepareSse(Response);

        var reader = _eventBus.Subscribe<ChannelCapacityChangedEvent>(
            e => isSuperadmin || string.Equals(e.OwnerUsername, username, StringComparison.Ordinal),
            HttpContext.RequestAborted);

        await SseEventWriter.StreamAsync(
            Response,
            reader,
            async ct =>
            {
                using var scope = _scopeFactory.CreateScope();
                var config = scope.ServiceProvider.GetRequiredService<IConfigService>();
                var result = config.ReadChannelRuntime(null);
                var payload = result.Payload ?? new ChannelRuntimeListResponse();
                var data = JsonSerializer.Serialize(payload);
                await Response.WriteAsync($"event: runtime\n", ct);
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            },
            HttpContext.RequestAborted);
    }

    private async Task WriteActiveChannelStream()
    {
        var user = RequireUser();
        var isSuperadmin = user.Role == "superadmin";
        var username = user.Username;

        ProxyStreamResponseWriter.PrepareSse(Response);

        var reader = _eventBus.Subscribe<ChannelCapacityChangedEvent>(
            e => isSuperadmin || string.Equals(e.OwnerUsername, username, StringComparison.Ordinal),
            HttpContext.RequestAborted);

        await SseEventWriter.StreamAsync(
            Response,
            reader,
            async ct =>
            {
                using var scope = _scopeFactory.CreateScope();
                var observability = scope.ServiceProvider.GetRequiredService<IObservabilityService>();
                var result = observability.ReadActiveChannelQueue();
                var payload = result.Payload ?? new ActiveChannelQueueResponse(string.Empty, []);
                var data = JsonSerializer.Serialize(payload);
                await Response.WriteAsync($"event: queue\n", ct);
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            },
            HttpContext.RequestAborted);
    }

    private async Task WriteRecentErrorsStream()
    {
        var user = RequireUser();
        var isSuperadmin = user.Role == "superadmin";
        var username = user.Username;

        ProxyStreamResponseWriter.PrepareSse(Response);

        var reader = _eventBus.Subscribe<RequestLogWrittenEvent>(
            e => e.IsError && (isSuperadmin || string.Equals(e.OwnerUsername, username, StringComparison.Ordinal)),
            HttpContext.RequestAborted);

        await SseEventWriter.StreamAsync(
            Response,
            reader,
            async ct =>
            {
                using var scope = _scopeFactory.CreateScope();
                var observability = scope.ServiceProvider.GetRequiredService<IObservabilityService>();
                var result = observability.ReadRecentErrors(5);
                var payload = result.Payload ?? [];
                var data = JsonSerializer.Serialize(payload);
                await Response.WriteAsync($"event: errors\n", ct);
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            },
            HttpContext.RequestAborted);
    }

    private async Task WriteLogsStream()
    {
        var user = RequireUser();
        var isSuperadmin = user.Role == "superadmin";
        var username = user.Username;

        ProxyStreamResponseWriter.PrepareSse(Response);

        var reader = _eventBus.Subscribe<RequestLogWrittenEvent>(
            e => isSuperadmin || string.Equals(e.OwnerUsername, username, StringComparison.Ordinal),
            HttpContext.RequestAborted);

        await SseEventWriter.StreamAsync(
            Response,
            reader,
            async ct =>
            {
                // 轻量通知：前端收到后自行刷新当前页（保留筛选与分页）
                var data = JsonSerializer.Serialize(new { });
                await Response.WriteAsync($"event: logs\n", ct);
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            },
            HttpContext.RequestAborted);
    }
}

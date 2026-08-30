using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using OpenCodex.Api.Infrastructure;
using OpenCodex.CoreBase.DTOs.Channels;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Events;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Services;

/// <summary>
/// 事件驱动 SSE 推送：初始快照 + 事件去抖 + 心跳，替代 while+Task.Delay 轮询。
/// </summary>
/// <remarks>
/// 快照与心跳由同一个循环串行写出。<see cref="HttpResponse"/> 的写入不是线程安全的，
/// 一旦心跳跑在独立任务里就会与快照交错写同一条连接，产生错帧并抛异常终止流。
/// </remarks>
public sealed class RealtimeStreamService : IRealtimeStreamService
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(15);

    private readonly IEventBus _eventBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkContext _workContext;

    public RealtimeStreamService(
        IWorkContext workContext,
        IEventBus eventBus,
        IServiceScopeFactory scopeFactory)
    {
        _workContext = workContext;
        _eventBus = eventBus;
        _scopeFactory = scopeFactory;
    }

    public async Task StreamChannelRuntimeAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        var user = _workContext.RequireUser();
        var isSuperadmin = user.Role == "superadmin";
        var username = user.Username;

        ProxyStreamResponseWriter.PrepareSse(response);

        var reader = _eventBus.Subscribe<ChannelCapacityChangedEvent>(
            e => isSuperadmin || string.Equals(e.OwnerUsername, username, StringComparison.Ordinal),
            cancellationToken);

        await StreamAsync(
            response,
            reader,
            async ct =>
            {
                using var scope = _scopeFactory.CreateScope();
                var channels = scope.ServiceProvider.GetRequiredService<IChannelService>();
                var result = channels.ReadChannelRuntime(null);
                var payload = result.Payload ?? new ChannelRuntimeListResponse();
                await WriteFrameAsync(response, "runtime", payload, ct);
            },
            cancellationToken);
    }

    public async Task StreamActiveChannelsAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        var user = _workContext.RequireUser();
        var isSuperadmin = user.Role == "superadmin";
        var username = user.Username;

        ProxyStreamResponseWriter.PrepareSse(response);

        var reader = _eventBus.Subscribe<ChannelCapacityChangedEvent>(
            e => isSuperadmin || string.Equals(e.OwnerUsername, username, StringComparison.Ordinal),
            cancellationToken);

        await StreamAsync(
            response,
            reader,
            async ct =>
            {
                using var scope = _scopeFactory.CreateScope();
                var observability = scope.ServiceProvider.GetRequiredService<IObservabilityService>();
                var result = observability.ReadActiveChannelQueue();
                var payload = result.Payload ?? new ActiveChannelQueueResponse(string.Empty, []);
                await WriteFrameAsync(response, "queue", payload, ct);
            },
            cancellationToken);
    }

    public async Task StreamRecentErrorsAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        var user = _workContext.RequireUser();
        var isSuperadmin = user.Role == "superadmin";
        var username = user.Username;

        ProxyStreamResponseWriter.PrepareSse(response);

        var reader = _eventBus.Subscribe<RequestLogWrittenEvent>(
            e => e.IsError && (isSuperadmin || string.Equals(e.OwnerUsername, username, StringComparison.Ordinal)),
            cancellationToken);

        await StreamAsync(
            response,
            reader,
            async ct =>
            {
                using var scope = _scopeFactory.CreateScope();
                var observability = scope.ServiceProvider.GetRequiredService<IObservabilityService>();
                var result = observability.ReadRecentErrors(5);
                var payload = result.Payload ?? [];
                await WriteFrameAsync(response, "errors", payload, ct);
            },
            cancellationToken);
    }

    public async Task StreamLogsAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        var user = _workContext.RequireUser();
        var isSuperadmin = user.Role == "superadmin";
        var username = user.Username;

        ProxyStreamResponseWriter.PrepareSse(response);

        var reader = _eventBus.Subscribe<RequestLogWrittenEvent>(
            e => isSuperadmin || string.Equals(e.OwnerUsername, username, StringComparison.Ordinal),
            cancellationToken);

        await StreamAsync(
            response,
            reader,
            async ct =>
            {
                // 轻量通知：前端收到后自行刷新当前页（保留筛选与分页）
                await WriteFrameAsync(response, "logs", new { }, ct);
            },
            cancellationToken);
    }

    private static async Task WriteFrameAsync(
        HttpResponse response,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(payload);
        await response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await response.WriteAsync($"data: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    internal async Task StreamAsync<TEvent>(
        HttpResponse response,
        ChannelReader<TEvent> eventReader,
        Func<CancellationToken, Task> pushSnapshot,
        CancellationToken cancellationToken)
    {
        await StreamAsync(
            response,
            eventReader,
            pushSnapshot,
            DefaultDebounce,
            DefaultHeartbeat,
            cancellationToken);
    }

    internal async Task StreamAsync<TEvent>(
        HttpResponse response,
        ChannelReader<TEvent> eventReader,
        Func<CancellationToken, Task> pushSnapshot,
        TimeSpan debounceDelay,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken)
    {
        try
        {
            // 初始快照
            await pushSnapshot(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                // 等待下一个事件，最长等一个心跳间隔；超时即写心跳保持连接活性。
                var hasData = await WaitForEventAsync(eventReader, heartbeatInterval, cancellationToken);
                if (hasData is null) break;

                if (hasData == false)
                {
                    await WriteHeartbeatAsync(response, cancellationToken);
                    continue;
                }

                // 去抖窗口：等一小段时间合并密集事件
                await Task.Delay(debounceDelay, cancellationToken);

                // 排空窗口内积压的事件
                while (eventReader.TryRead(out _)) { }

                // 回查并推送一帧快照
                await pushSnapshot(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开或服务端关闭：正常收敛，不向上抛。
        }
    }

    /// <summary>
    /// 等待下一个事件。
    /// </summary>
    /// <returns>
    /// <c>true</c> 表示有事件可读；<c>false</c> 表示等待超时（该发心跳）；
    /// <c>null</c> 表示事件流已完成，应结束推送。
    /// </returns>
    private static async Task<bool?> WaitForEventAsync<TEvent>(
        ChannelReader<TEvent> eventReader,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(heartbeatInterval);

        try
        {
            // WaitToReadAsync 返回 false 表示 channel 已完成（订阅被移除），此时应结束推送。
            return await eventReader.WaitToReadAsync(waitCts.Token) ? true : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 仅心跳定时器到点，连接仍然活着。
            return false;
        }
    }

    private static async Task WriteHeartbeatAsync(
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        // 具名事件而非 ": comment"：EventSource 会丢弃注释行，前端无法据此判断连接活性。
        await response.WriteAsync("event: heartbeat\n", cancellationToken);
        await response.WriteAsync("data: {}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

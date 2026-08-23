using System.Threading.Channels;

namespace OpenCodex.Api.Infrastructure;

/// <summary>
/// 事件驱动 SSE 推送辅助类。初始快照 + 事件去抖 + 心跳,替代 while+Task.Delay 轮询。
/// </summary>
public static class SseEventWriter
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 基于事件流的 SSE 推送。连接时推一次快照,后续事件触发去抖后回查推送,空闲发心跳。
    /// </summary>
    /// <typeparam name="TEvent">事件类型。</typeparam>
    /// <param name="response">HTTP 响应(已 PrepareSse)。</param>
    /// <param name="eventReader">事件总线订阅的 ChannelReader。</param>
    /// <param name="pushSnapshot">回查并推送一帧快照的回调。</param>
    /// <param name="cancellationToken">连接取消令牌。</param>
    public static async Task StreamAsync<TEvent>(
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

    public static async Task StreamAsync<TEvent>(
        HttpResponse response,
        ChannelReader<TEvent> eventReader,
        Func<CancellationToken, Task> pushSnapshot,
        TimeSpan debounceDelay,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken)
    {
        // 初始快照
        await pushSnapshot(cancellationToken);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 心跳后台任务
        var heartbeat = Task.Run(() => RunHeartbeatAsync(response, heartbeatInterval, heartbeatCts.Token), heartbeatCts.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 阻塞等待下一个事件;无事件时挂起零开销
                bool hasData;
                try
                {
                    hasData = await eventReader.WaitToReadAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!hasData) break;

                // 去抖窗口:等一小段时间合并密集事件
                try
                {
                    await Task.Delay(debounceDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // 排空窗口内积压的事件
                while (eventReader.TryRead(out _)) { }

                // 回查并推送一帧快照
                try
                {
                    await pushSnapshot(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat; } catch { }
        }
    }

    private static async Task RunHeartbeatAsync(
        HttpResponse response,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                // SSE 注释行,客户端忽略
                await response.WriteAsync(": heartbeat\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
            }
            catch
            {
                return;
            }
        }
    }
}

using System.Threading.Channels;

namespace OpenCodex.Api.Infrastructure;

/// <summary>
/// 事件驱动 SSE 推送辅助类。初始快照 + 事件去抖 + 心跳,替代 while+Task.Delay 轮询。
/// </summary>
/// <remarks>
/// 快照与心跳由同一个循环串行写出。<see cref="HttpResponse"/> 的写入不是线程安全的,
/// 一旦心跳跑在独立任务里就会与快照交错写同一条连接,产生错帧并抛异常终止流。
/// </remarks>
public static class SseEventWriter
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(15);

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
        try
        {
            // 初始快照
            await pushSnapshot(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                // 等待下一个事件,最长等一个心跳间隔;超时即写心跳保持连接活性。
                var hasData = await WaitForEventAsync(eventReader, heartbeatInterval, cancellationToken);
                if (hasData is null) break;

                if (hasData == false)
                {
                    await WriteHeartbeatAsync(response, cancellationToken);
                    continue;
                }

                // 去抖窗口:等一小段时间合并密集事件
                await Task.Delay(debounceDelay, cancellationToken);

                // 排空窗口内积压的事件
                while (eventReader.TryRead(out _)) { }

                // 回查并推送一帧快照
                await pushSnapshot(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开或服务端关闭:正常收敛,不向上抛。
        }
    }

    /// <summary>
    /// 等待下一个事件。
    /// </summary>
    /// <returns>
    /// <c>true</c> 表示有事件可读;<c>false</c> 表示等待超时(该发心跳);
    /// <c>null</c> 表示事件流已完成,应结束推送。
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
            // WaitToReadAsync 返回 false 表示 channel 已完成(订阅被移除),此时应结束推送。
            return await eventReader.WaitToReadAsync(waitCts.Token) ? true : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 仅心跳定时器到点,连接仍然活着。
            return false;
        }
    }

    private static async Task WriteHeartbeatAsync(
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        // 具名事件而非 ": comment":EventSource 会丢弃注释行,前端无法据此判断连接活性。
        await response.WriteAsync("event: heartbeat\n", cancellationToken);
        await response.WriteAsync("data: {}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

namespace OpenCodex.Api.Services;

/// <summary>
/// 管理台实时状态 SSE 推送服务：渠道容量、处理队列、近期错误、日志更新。
/// </summary>
public interface IRealtimeStreamService
{
    Task StreamChannelRuntimeAsync(HttpResponse response, CancellationToken cancellationToken);

    Task StreamActiveChannelsAsync(HttpResponse response, CancellationToken cancellationToken);

    Task StreamRecentErrorsAsync(HttpResponse response, CancellationToken cancellationToken);

    Task StreamLogsAsync(HttpResponse response, CancellationToken cancellationToken);
}

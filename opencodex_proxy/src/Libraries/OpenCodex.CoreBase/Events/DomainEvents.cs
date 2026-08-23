namespace OpenCodex.CoreBase.Events;

/// <summary>
/// 渠道并发容量变化事件(请求开始占位或释放槽位时发布)。
/// </summary>
public sealed class ChannelCapacityChangedEvent
{
    public required string OwnerUsername { get; init; }

    public string? ChannelId { get; init; }
}

/// <summary>
/// 请求日志写入完成事件。
/// </summary>
public sealed class RequestLogWrittenEvent
{
    public required string OwnerUsername { get; init; }

    public Guid LogId { get; init; }

    /// <summary>
    /// 是否为错误请求(状态码 >= 400 或生命周期标记为 failed)。
    /// </summary>
    public bool IsError { get; init; }
}

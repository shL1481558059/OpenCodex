using System.Threading.Channels;

namespace OpenCodex.CoreBase.Events;

/// <summary>
/// 进程内 + Redis pub/sub 双层事件总线。
/// </summary>
/// <remarks>
/// 发布方 fire-and-forget,不阻塞调用链;订阅方通过 <see cref="ChannelReader{T}"/> 异步消费。
/// Redis 不可用时降级为纯进程内,行为与 <see cref="Caching.ICacheService"/> 一致。
/// </remarks>
public interface IEventBus
{
    /// <summary>
    /// 订阅事件,返回 <see cref="ChannelReader{T}"/> 供 <c>await foreach</c> 消费。
    /// </summary>
    /// <param name="filter">事件过滤谓词;返回 false 的事件不会投递给此订阅者。</param>
    /// <param name="cancellationToken">取消时自动移除订阅并完成 reader。</param>
    ChannelReader<TEvent> Subscribe<TEvent>(
        Func<TEvent, bool> filter,
        CancellationToken cancellationToken);

    /// <summary>
    /// 发布事件(同步 fire-and-forget)。进程内扇出 + Redis pub/sub 跨实例广播。
    /// </summary>
    void Publish<TEvent>(TEvent evt) where TEvent : notnull;
}

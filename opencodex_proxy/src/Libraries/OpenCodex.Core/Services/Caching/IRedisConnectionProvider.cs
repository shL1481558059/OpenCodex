using StackExchange.Redis;

namespace OpenCodex.Core.Services.Caching;

/// <summary>
/// 提供全局唯一的 Redis 连接(<see cref="ConnectionMultiplexer"/>)访问入口。
/// </summary>
/// <remarks>
/// 采用懒加载 + 双检锁单例,连接创建时 <c>AbortOnConnectFail=false</c>,
/// 连接失败不阻塞服务启动。调用方应先检查 <see cref="IsAvailable"/> 再取库/发布。
/// </remarks>
public interface IRedisConnectionProvider
{
    /// <summary>
    /// 获取 Redis 是否可用。连接串为空或连接失败时为 false,调用方据此降级为纯 L1。
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 获取全局键前缀(形如 <c>opencodex</c>),实现在挂载到 key 时统一追加 <c>":"</c> 分隔。
    /// </summary>
    string KeyPrefix { get; }

    /// <summary>
    /// 获取带前缀隔离的数据库句柄;当 Redis 不可用时返回 null。
    /// </summary>
    /// <param name="db">目标逻辑库,-1 表示默认库。</param>
    IDatabase? GetDatabase(int db = -1);

    /// <summary>
    /// 获取发布/订阅句柄,用于跨实例缓存失效广播;不可用时返回 null。
    /// </summary>
    ISubscriber? GetSubscriber();
}

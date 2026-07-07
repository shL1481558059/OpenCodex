namespace OpenCodex.CoreBase.Caching;

/// <summary>
/// 两级缓存(L1 进程内内存 + L2 Redis)的门面抽象。
/// </summary>
/// <remarks>
/// 该接口只使用 BCL 类型,不暴露任何 Redis 具体类型,业务服务仅依赖此接口。
/// 具体实现负责:读路径逐层命中并回写,写路径本地失效 + Redis 失效 + 跨实例广播。
/// 当 Redis 不可用时,实现应自动降级为纯 L1,不得抛出异常。
/// </remarks>
public interface ICacheService
{
    /// <summary>
    /// 读缓存;未命中时执行 <paramref name="factory"/> 回源,并逐层写回。
    /// </summary>
    /// <typeparam name="T">缓存值类型。</typeparam>
    /// <param name="key">逻辑缓存键(不含全局前缀,前缀由实现统一挂载)。</param>
    /// <param name="factory">回源委托,仅在两级缓存都未命中时调用。</param>
    /// <param name="ttl">过期时长;为 null 时使用实现的默认 TTL。</param>
    /// <returns>缓存或回源得到的值;当 <paramref name="factory"/> 返回 null 时不写缓存并返回 null。</returns>
    Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T?>> factory, TimeSpan? ttl = null);

    /// <summary>
    /// 失效单个键:删除本地 L1、删除 Redis L2,并广播通知其它实例删除各自 L1。
    /// </summary>
    /// <param name="key">逻辑缓存键(不含全局前缀)。</param>
    Task RemoveAsync(string key);

    /// <summary>
    /// 批量失效多个键。语义与 <see cref="RemoveAsync(string)"/> 相同。
    /// </summary>
    /// <param name="keys">逻辑缓存键集合(不含全局前缀)。</param>
    Task RemoveAsync(IEnumerable<string> keys);
}

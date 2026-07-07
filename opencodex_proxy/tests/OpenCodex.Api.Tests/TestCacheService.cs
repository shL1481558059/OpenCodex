using OpenCodex.CoreBase.Caching;

namespace OpenCodex.Api.Tests;

/// <summary>
/// 测试用直通透缓存:不做任何缓存,GetOrCreateAsync 直接执行 factory 回源,
/// Remove 为空操作。用于在测试中提供 ICacheService 而不改变原有查询行为。
/// </summary>
internal sealed class TestCacheService : ICacheService
{
    public Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T?>> factory, TimeSpan? ttl = null)
    {
        return factory();
    }

    public Task RemoveAsync(string key) => Task.CompletedTask;

    public Task RemoveAsync(IEnumerable<string> keys) => Task.CompletedTask;
}

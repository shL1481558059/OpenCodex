using System.Diagnostics;
using OpenCodex.Core.Services.Caching;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class RedisConnectionProviderTests
{
    [Fact]
    public void GetDatabase_WhenConnectionStringMissing_ReturnsNull()
    {
        using var provider = new RedisConnectionProvider(null, null);

        Assert.False(provider.IsAvailable);
        Assert.Null(provider.GetDatabase());
        Assert.Null(provider.GetSubscriber());
    }

    [Fact]
    public void GetDatabase_WhenRedisUnreachable_ReturnsNullWithoutBlockingCaller()
    {
        // 指向不可达端口:构造期最多等待有限时间,之后所有调用必须立即降级,
        // 不能把调用线程耗在等待 Redis 连接上(否则并发请求会拖垮线程池)。
        using var provider = new RedisConnectionProvider(
            "127.0.0.1:1,connectTimeout=500,abortConnect=false",
            "opencodex-test");

        var stopwatch = Stopwatch.StartNew();
        var database = provider.GetDatabase();
        var subscriber = provider.GetSubscriber();
        var available = provider.IsAvailable;
        stopwatch.Stop();

        Assert.Null(database);
        Assert.Null(subscriber);
        Assert.False(available);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 500,
            $"调用路径耗时 {stopwatch.ElapsedMilliseconds} ms,应快速降级而不是等待 Redis。");
    }
}

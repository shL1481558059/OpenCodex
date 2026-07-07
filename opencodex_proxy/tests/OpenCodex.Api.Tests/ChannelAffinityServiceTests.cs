using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ChannelAffinityServiceTests
{
    [Fact]
    public async Task Remember_ThenGet_ReturnsChannelId()
    {
        var service = new ChannelAffinityService();

        await service.RememberAsync("admin", "key-1", "channel-a");

        Assert.Equal("channel-a", await service.GetPreferredChannelIdAsync("admin", "key-1"));
    }

    [Fact]
    public async Task Get_UnknownKey_ReturnsNull()
    {
        var service = new ChannelAffinityService();

        Assert.Null(await service.GetPreferredChannelIdAsync("admin", "missing"));
    }

    [Fact]
    public async Task Get_EmptyStickyKey_ReturnsNull()
    {
        var service = new ChannelAffinityService();

        await service.RememberAsync("admin", string.Empty, "channel-a");

        Assert.Null(await service.GetPreferredChannelIdAsync("admin", string.Empty));
    }

    [Fact]
    public async Task Remember_EmptyChannelId_IsIgnored()
    {
        var service = new ChannelAffinityService();

        await service.RememberAsync("admin", "key-1", string.Empty);

        Assert.Null(await service.GetPreferredChannelIdAsync("admin", "key-1"));
    }

    [Fact]
    public async Task DifferentOwners_DoNotShareMapping()
    {
        var service = new ChannelAffinityService();

        await service.RememberAsync("alice", "key-1", "channel-a");

        Assert.Equal("channel-a", await service.GetPreferredChannelIdAsync("alice", "key-1"));
        Assert.Null(await service.GetPreferredChannelIdAsync("bob", "key-1"));
    }

    [Fact]
    public async Task Get_AfterTtlElapsed_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelAffinityService(TimeSpan.FromMinutes(10), () => now);

        await service.RememberAsync("admin", "key-1", "channel-a");
        now = now.AddMinutes(11);

        Assert.Null(await service.GetPreferredChannelIdAsync("admin", "key-1"));
    }

    [Fact]
    public async Task Get_BeforeTtl_SlidesExpiration()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelAffinityService(TimeSpan.FromMinutes(10), () => now);

        await service.RememberAsync("admin", "key-1", "channel-a");

        // 在过期前访问，应刷新有效期。
        now = now.AddMinutes(9);
        Assert.Equal("channel-a", await service.GetPreferredChannelIdAsync("admin", "key-1"));

        // 距上次访问再过 9 分钟（未超过 TTL），仍应有效。
        now = now.AddMinutes(9);
        Assert.Equal("channel-a", await service.GetPreferredChannelIdAsync("admin", "key-1"));
    }

    [Fact]
    public async Task Remember_Again_OverwritesChannelId()
    {
        var service = new ChannelAffinityService();

        await service.RememberAsync("admin", "key-1", "channel-a");
        await service.RememberAsync("admin", "key-1", "channel-b");

        Assert.Equal("channel-b", await service.GetPreferredChannelIdAsync("admin", "key-1"));
    }
}

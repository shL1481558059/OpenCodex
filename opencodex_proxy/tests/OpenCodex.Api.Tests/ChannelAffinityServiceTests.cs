using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ChannelAffinityServiceTests
{
    [Fact]
    public void Remember_ThenGet_ReturnsChannelId()
    {
        var service = new ChannelAffinityService();

        service.Remember("admin", "key-1", "channel-a");

        Assert.Equal("channel-a", service.GetPreferredChannelId("admin", "key-1"));
    }

    [Fact]
    public void Get_UnknownKey_ReturnsNull()
    {
        var service = new ChannelAffinityService();

        Assert.Null(service.GetPreferredChannelId("admin", "missing"));
    }

    [Fact]
    public void Get_EmptyStickyKey_ReturnsNull()
    {
        var service = new ChannelAffinityService();

        service.Remember("admin", string.Empty, "channel-a");

        Assert.Null(service.GetPreferredChannelId("admin", string.Empty));
    }

    [Fact]
    public void Remember_EmptyChannelId_IsIgnored()
    {
        var service = new ChannelAffinityService();

        service.Remember("admin", "key-1", string.Empty);

        Assert.Null(service.GetPreferredChannelId("admin", "key-1"));
    }

    [Fact]
    public void DifferentOwners_DoNotShareMapping()
    {
        var service = new ChannelAffinityService();

        service.Remember("alice", "key-1", "channel-a");

        Assert.Equal("channel-a", service.GetPreferredChannelId("alice", "key-1"));
        Assert.Null(service.GetPreferredChannelId("bob", "key-1"));
    }

    [Fact]
    public void Get_AfterTtlElapsed_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelAffinityService(TimeSpan.FromMinutes(10), () => now);

        service.Remember("admin", "key-1", "channel-a");
        now = now.AddMinutes(11);

        Assert.Null(service.GetPreferredChannelId("admin", "key-1"));
    }

    [Fact]
    public void Get_BeforeTtl_SlidesExpiration()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ChannelAffinityService(TimeSpan.FromMinutes(10), () => now);

        service.Remember("admin", "key-1", "channel-a");

        // 在过期前访问，应刷新有效期。
        now = now.AddMinutes(9);
        Assert.Equal("channel-a", service.GetPreferredChannelId("admin", "key-1"));

        // 距上次访问再过 9 分钟（未超过 TTL），仍应有效。
        now = now.AddMinutes(9);
        Assert.Equal("channel-a", service.GetPreferredChannelId("admin", "key-1"));
    }

    [Fact]
    public void Remember_Again_OverwritesChannelId()
    {
        var service = new ChannelAffinityService();

        service.Remember("admin", "key-1", "channel-a");
        service.Remember("admin", "key-1", "channel-b");

        Assert.Equal("channel-b", service.GetPreferredChannelId("admin", "key-1"));
    }
}


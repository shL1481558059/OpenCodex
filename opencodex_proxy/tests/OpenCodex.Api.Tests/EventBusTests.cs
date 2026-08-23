using System.Threading.Channels;
using OpenCodex.Core.Services.Events;
using OpenCodex.CoreBase.Events;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class EventBusTests : IDisposable
{
    private readonly EventBus _bus = new();

    [Fact]
    public async Task Subscribe_ReceivesPublishedEvent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = _bus.Subscribe<ChannelCapacityChangedEvent>(_ => true, cts.Token);

        _bus.Publish(new ChannelCapacityChangedEvent
        {
            OwnerUsername = "alice",
            ChannelId = "ch1"
        });

        var hasData = await reader.WaitToReadAsync(cts.Token);
        Assert.True(hasData);
        Assert.True(reader.TryRead(out var evt));
        Assert.Equal("alice", evt.OwnerUsername);
        Assert.Equal("ch1", evt.ChannelId);
    }

    [Fact]
    public async Task Subscribe_FilterExcludesNonMatchingEvents()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = _bus.Subscribe<ChannelCapacityChangedEvent>(
            e => e.OwnerUsername == "alice",
            cts.Token);

        // 发布不匹配的事件
        _bus.Publish(new ChannelCapacityChangedEvent
        {
            OwnerUsername = "bob",
            ChannelId = "ch2"
        });

        // 发布匹配的事件
        _bus.Publish(new ChannelCapacityChangedEvent
        {
            OwnerUsername = "alice",
            ChannelId = "ch3"
        });

        var hasData = await reader.WaitToReadAsync(cts.Token);
        Assert.True(hasData);
        Assert.True(reader.TryRead(out var evt));
        Assert.Equal("alice", evt.OwnerUsername);
        Assert.Equal("ch3", evt.ChannelId);

        // 确保 bob 的事件被过滤掉(只剩 alice 的事件)
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public async Task Subscribe_MultipleSubscribersAllReceiveEvent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader1 = _bus.Subscribe<RequestLogWrittenEvent>(_ => true, cts.Token);
        var reader2 = _bus.Subscribe<RequestLogWrittenEvent>(_ => true, cts.Token);

        _bus.Publish(new RequestLogWrittenEvent
        {
            OwnerUsername = "alice",
            LogId = Guid.NewGuid(),
            IsError = true
        });

        Assert.True(await reader1.WaitToReadAsync(cts.Token));
        Assert.True(reader1.TryRead(out var evt1));
        Assert.True(evt1.IsError);

        Assert.True(await reader2.WaitToReadAsync(cts.Token));
        Assert.True(reader2.TryRead(out var evt2));
        Assert.True(evt2.IsError);
    }

    [Fact]
    public async Task Subscribe_CancellationRemovesSubscription()
    {
        using var cts = new CancellationTokenSource();
        var reader = _bus.Subscribe<ChannelCapacityChangedEvent>(_ => true, cts.Token);

        cts.Cancel();

        // 取消后 reader 应完成
        Assert.False(await reader.WaitToReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Subscribe_DifferentEventTypesDoNotCrossContaminate()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var capacityReader = _bus.Subscribe<ChannelCapacityChangedEvent>(_ => true, cts.Token);
        var logReader = _bus.Subscribe<RequestLogWrittenEvent>(_ => true, cts.Token);

        _bus.Publish(new ChannelCapacityChangedEvent
        {
            OwnerUsername = "alice",
            ChannelId = "ch1"
        });

        // 容量事件只到 capacityReader
        Assert.True(await capacityReader.WaitToReadAsync(cts.Token));
        Assert.True(capacityReader.TryRead(out _));

        // logReader 不应有数据
        Assert.False(logReader.TryRead(out _));
    }

    [Fact]
    public async Task Publish_DoesNotBlockWhenNoSubscribers()
    {
        // 无订阅者时发布不应抛异常
        _bus.Publish(new ChannelCapacityChangedEvent
        {
            OwnerUsername = "alice",
            ChannelId = "ch1"
        });

        // 验证不阻塞:后续订阅能正常工作
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = _bus.Subscribe<ChannelCapacityChangedEvent>(_ => true, cts.Token);
        _bus.Publish(new ChannelCapacityChangedEvent
        {
            OwnerUsername = "alice",
            ChannelId = "ch2"
        });
        Assert.True(await reader.WaitToReadAsync(cts.Token));
    }

    [Fact]
    public async Task Subscribe_BoundedChannelDropsOldestWhenFull()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = _bus.Subscribe<ChannelCapacityChangedEvent>(_ => true, cts.Token);

        // 发布超过容量(64)的事件
        for (var i = 0; i < 100; i++)
        {
            _bus.Publish(new ChannelCapacityChangedEvent
            {
                OwnerUsername = "alice",
                ChannelId = $"ch{i}"
            });
        }

        // 应能读到部分事件(DropOldest 保证不阻塞发布)
        var count = 0;
        while (reader.TryRead(out _))
        {
            count++;
        }
        Assert.True(count > 0);
    }

    public void Dispose() => _bus.Dispose();
}

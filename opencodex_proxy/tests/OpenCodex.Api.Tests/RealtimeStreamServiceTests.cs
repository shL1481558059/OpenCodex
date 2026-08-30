using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenCodex.Api.Services;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Events;
using OpenCodex.CoreBase.Services;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class RealtimeStreamServiceTests
{
    [Fact]
    public async Task StreamAsync_IdleStream_WritesHeartbeatAfterInterval()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        using var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<string>();

        // 心跳间隔 20ms,让流空等约 80ms(4 个心跳周期)再取消,确保至少写出一帧心跳。
        var streamTask = CreateService().StreamAsync(
            context.Response,
            channel.Reader,
            _ => Task.CompletedTask,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(20),
            cts.Token);

        await Task.Delay(80);
        cts.Cancel();
        await streamTask;

        var body = ReadBody(context.Response);
        Assert.Contains("event: heartbeat\ndata: {}\n\n", body);
    }

    [Fact]
    public async Task StreamAsync_EventPushesSnapshotFrame_AfterDebounce()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var channel = Channel.CreateUnbounded<string>();
        var snapshotCallCount = 0;

        var streamTask = CreateService().StreamAsync(
            context.Response,
            channel.Reader,
            async ct =>
            {
                snapshotCallCount++;
                await context.Response.WriteAsync("event: runtime\ndata: {\"i\":1}\n\n", ct);
            },
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        // 初始快照已写。
        await channel.Writer.WriteAsync("evt");
        await Task.Delay(50);
        channel.Writer.Complete();
        await streamTask;

        Assert.True(snapshotCallCount >= 2);
        var body = ReadBody(context.Response);
        Assert.Contains("event: runtime\ndata: {\"i\":1}\n\n", body);
    }

    [Fact]
    public async Task StreamAsync_ChannelCompletion_ExitsCleanly()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var channel = Channel.CreateUnbounded<string>();
        channel.Writer.Complete();

        await CreateService().StreamAsync(
            context.Response,
            channel.Reader,
            _ => Task.CompletedTask,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        // 完成的 channel 立即退出,不抛异常。
        var body = ReadBody(context.Response);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task StreamAsync_Cancellation_ReturnsWithoutThrowing()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        using var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<string>();

        var streamTask = CreateService().StreamAsync(
            context.Response,
            channel.Reader,
            async ct => { await Task.Delay(5000, ct); },
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
            cts.Token);

        cts.CancelAfter(50);
        await streamTask;

        // 不抛异常即视为通过。
        Assert.True(context.Response.Body.Length >= 0);
    }

    /// <summary>
    /// 2.3 的回归防线:心跳与快照由同一个循环串行写,任何帧都不应被交错撕裂。
    /// 用一个会主动 yield 的 stream 强制出真实的异步交错窗口,短心跳间隔 + 高频事件。
    /// </summary>
    [Fact]
    public async Task StreamAsync_ConcurrentHeartbeatAndSnapshots_ProduceIntactFrames()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new YieldingMemoryStream();

        var channel = Channel.CreateUnbounded<string>();
        var snapshotIndex = -1;

        var streamTask = CreateService().StreamAsync(
            context.Response,
            channel.Reader,
            async ct =>
            {
                snapshotIndex++;
                await context.Response.WriteAsync($"event: runtime\ndata: {{\"i\":{snapshotIndex}}}\n\n", ct);
            },
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(2),
            CancellationToken.None);

        // 短时间内灌入大量事件,逼出心跳与快照的并发窗口。
        for (var i = 0; i < 500; i++)
        {
            await channel.Writer.WriteAsync("evt");
            await Task.Yield();
        }

        channel.Writer.Complete();
        await streamTask;

        var body = ReadBody(context.Response);
        var frames = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.NotEmpty(frames);
        foreach (var frame in frames)
        {
            // 每帧必须是严格的 "event: <name>\ndata: <json>" 两行结构。
            var lines = frame.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines.Length == 2, $"帧结构被撕裂: {frame}");
            Assert.StartsWith("event: ", lines[0]);
            Assert.True(lines[0] == "event: runtime" || lines[0] == "event: heartbeat");
            Assert.StartsWith("data: ", lines[1]);

            if (lines[0] == "event: heartbeat")
            {
                Assert.Equal("data: {}", lines[1]);
            }
        }
    }

    [Fact]
    public async Task StreamLogsAsync_WritesLogsFrameOnEvent()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var eventBus = new StubEventBus();
        var service = new RealtimeStreamService(
            new StubWorkContext(),
            eventBus,
            new StubScopeFactory());

        using var cts = new CancellationTokenSource();
        var streamTask = service.StreamLogsAsync(context.Response, cts.Token);

        // 等待订阅建立,然后发布一个日志事件。
        await Task.Delay(20);
        eventBus.Publish(new RequestLogWrittenEvent
        {
            OwnerUsername = "admin",
            IsError = false
        });
        await Task.Delay(50);
        cts.Cancel();
        await streamTask;

        var body = ReadBody(context.Response);
        Assert.Contains("event: logs\n", body);
    }

    private static RealtimeStreamService CreateService()
    {
        return new RealtimeStreamService(
            new StubWorkContext(),
            new StubEventBus(),
            new StubScopeFactory());
    }

    private static string ReadBody(HttpResponse response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 在每次写入后让出一次,放大并发交错窗口,使帧完整性回归测试更可靠。
    /// </summary>
    private sealed class YieldingMemoryStream : MemoryStream
    {
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await base.WriteAsync(buffer, offset, count, cancellationToken);
            await Task.Yield();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            await Task.Yield();
        }
    }

    private sealed class StubWorkContext : IWorkContext
    {
        private static readonly SessionUser User = new(Guid.NewGuid(), "admin", "superadmin", true);

        public SessionUser? CurrentUser => User;
        public bool IsSignedIn => true;
        public bool IsSuperadmin => true;

        public SessionUser RequireUser() => User;
        public SessionUser RequireSuperadmin() => User;
    }

    private sealed class StubEventBus : IEventBus
    {
        private readonly object _lock = new();
        private readonly Dictionary<Type, object> _writers = new();

        public ChannelReader<TEvent> Subscribe<TEvent>(
            Func<TEvent, bool> filter,
            CancellationToken cancellationToken)
        {
            var channel = Channel.CreateUnbounded<TEvent>();
            lock (_lock)
            {
                _writers[typeof(TEvent)] = channel.Writer;
            }
            return channel.Reader;
        }

        public void Publish<TEvent>(TEvent evt) where TEvent : notnull
        {
            lock (_lock)
            {
                if (_writers.TryGetValue(typeof(TEvent), out var writer))
                {
                    ((ChannelWriter<TEvent>)writer).TryWrite(evt);
                }
            }
        }
    }

    private sealed class StubScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new StubScope();

        private sealed class StubScope : IServiceScope
        {
            public IServiceProvider ServiceProvider => new ServiceCollection().BuildServiceProvider();

            public void Dispose()
            {
            }
        }
    }
}

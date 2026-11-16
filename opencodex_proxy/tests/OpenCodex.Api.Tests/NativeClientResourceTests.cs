using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class NativeClientResourceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "native-resources-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Clock _clock = new();

    [Fact]
    public async Task ExpiredMemorySessionAndReplayRestoreFromDisk()
    {
        using var store = Store(new() { SessionTtlSeconds = 1, CleanupIntervalSeconds = 3600 });
        var session = await Session(store);
        var projection = await store.ProjectRequestAsync(session, Request("question"), "turn", default);
        await store.CommitResponseAsync(session, projection, Response("r1", "answer"), default);
        Assert.Single(Directory.GetFiles(_directory, "session.json", SearchOption.AllDirectories));

        _clock.Advance(2);
        await store.CleanupAsync();
        Assert.Equal(0, (await store.ResourceUsageAsync()).Sessions);

        var restored = Assert.IsType<NativeClientSession>(await store.TryGetAsync(_owner, "root", default));
        Assert.Equal("r1", (await store.ProjectRequestAsync(restored, Request("question"), "turn", default)).ReplayResponse!["id"]);
        var continuation = await store.ProjectRequestAsync(restored,
            new() { ["model"] = "test", ["previous_response_id"] = "r1", ["input"] = "next" }, "next", default);
        Assert.Equal(3, JsonDictionaryValue.List(continuation.Payload, "input").Count);
    }

    [Fact]
    public async Task NewStoreRestoresSessionAndCommittedResponse()
    {
        using (var store = Store())
        {
            var session = await Session(store);
            var projection = await store.ProjectRequestAsync(session, Request(), "turn", default);
            await store.CommitResponseAsync(session, projection, Response("r1"), default);
        }

        using var restarted = Store();
        var restored = await Session(restarted);
        Assert.Equal("r1", (await restarted.ProjectRequestAsync(restored, Request(), "turn", default)).ReplayResponse!["id"]);
    }

    [Fact]
    public async Task ActiveWaitersPinTheGroupAndReleaseEveryReference()
    {
        using var store = Store(new() { MaxWaitersPerSession = 1000, SessionTtlSeconds = 1, CleanupIntervalSeconds = 3600 });
        var session = await Session(store);
        var active = 0;
        var maximum = 0;
        var success = 0;
        var cancelled = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(async () =>
        {
            using var stop = new CancellationTokenSource();
            if (index % 3 == 0) stop.Cancel();
            try
            {
                await using var lease = await store.AcquireRequestAsync(session, stop.Token);
                var current = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref maximum, Math.Max(Volatile.Read(ref maximum), current));
                await store.CleanupAsync();
                await Task.Yield();
                Interlocked.Decrement(ref active);
                Interlocked.Increment(ref success);
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); }
        })));
        Assert.Equal(1, maximum);
        Assert.True(success > 0);
        Assert.True(cancelled > 0);
        Assert.Equal(0, (await store.ResourceUsageAsync()).References);

        _clock.Advance(2);
        await store.CleanupAsync();
        Assert.Equal(0, (await store.ResourceUsageAsync()).Sessions);
        Assert.NotNull(await store.TryGetAsync(_owner, "root", default));
    }

    [Fact]
    public async Task FailedIdentityWriteDoesNotLeakGroupsOrGates()
    {
        await File.WriteAllTextAsync(_directory, "not a directory");
        try
        {
            using var store = Store();
            for (var index = 0; index < 20; index++)
                await Assert.ThrowsAnyAsync<IOException>(() => Session(store, "failed" + index));
            var stats = await store.ResourceUsageAsync();
            Assert.Equal(0, stats.Groups);
            Assert.Equal(0, stats.Sessions);
            Assert.Equal(0, stats.References);
        }
        finally { File.Delete(_directory); }
    }

    private NativeClientSessionStore Store(NativeClientCacheOptions? options = null)
        => new(_directory, options ?? new() { CleanupIntervalSeconds = 3600 }, _clock);

    private Task<NativeClientSession> Session(NativeClientSessionStore store, string name = "root")
        => store.ResolveAsync(_owner, new() { ThreadId = name, RootThreadId = name, AgentName = "/root" }, 1, default);

    private static D Request(string text = "question") => new() { ["model"] = "test", ["input"] = text };

    private static D Response(string id, string text = "answer") => new()
    {
        ["id"] = id,
        ["status"] = "completed",
        ["output"] = new List<object?>
        {
            new D
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new List<object?> { new D { ["type"] = "output_text", ["text"] = text } }
            }
        }
    };

    private sealed class Clock : TimeProvider
    {
        private long _ticks = DateTimeOffset.Parse("2026-01-01T00:00:00Z").UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(int seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

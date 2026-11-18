using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using System.Text.Json;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class NativeClientLargePayloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ocxp-native-large-" + Guid.NewGuid().ToString("N"));

    [Fact(Timeout = 30_000)]
    public async Task LargeHistoryProjectionDoesNotMaterializeRepeatedWholePayloadCopies()
    {
        using var store = new NativeClientSessionStore(_directory);
        var owner = Guid.NewGuid();
        var session = await store.ResolveAsync(owner, new MultiAgentClientIdentity
        {
            ThreadId = "large-thread",
            RootThreadId = "large-thread",
            AgentName = "/root"
        }, 1, default);
        var input = Enumerable.Range(0, 720)
            .Select(index => (object?)new D
            {
                ["type"] = "message",
                ["role"] = index % 2 == 0 ? "user" : "assistant",
                ["content"] = index == 360 ? new string('x', 8 * 1024 * 1024) : "history-" + index
            })
            .ToList();
        var request = new D { ["model"] = "test", ["input"] = input };

        var before = GC.GetTotalAllocatedBytes(precise: true);
        _ = await store.ProjectRequestAsync(session, request, "large-turn", default);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.True(allocated < 64L * 1024 * 1024,
            $"Large request projection allocated {allocated / (1024d * 1024d):F1} MiB.");
    }

    [Fact(Timeout = 30_000)]
    public async Task LargeHistoryNativeEntryMaterializesOnceWithinTheRequestBudget()
    {
        var input = Enumerable.Range(0, 720)
            .Select(index => (object?)new D
            {
                ["type"] = "message",
                ["role"] = index % 2 == 0 ? "user" : "assistant",
                ["content"] = index == 360 ? new string('x', 8 * 1024 * 1024) : "history-" + index
            })
            .ToList();
        var request = new D { ["model"] = "test", ["input"] = input };
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(request);

        using var store = new NativeClientSessionStore(_directory);
        var session = await store.ResolveAsync(Guid.NewGuid(), Identity(), 1, default);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        await using var payload = JsonPayload.FromOwnedUtf8(utf8);
        var materialized = await payload.MaterializeObjectAsync();
        var projection = await store.ProjectRequestAsync(session, materialized!, "large-entry", default);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(720, JsonDictionaryValue.List(projection.Payload, "input").Count);
        Assert.True(allocated < 128L * 1024 * 1024,
            $"Large native request entry allocated {allocated / (1024d * 1024d):F1} MiB.");
    }

    [Fact]
    public async Task CommittedResponseUsesManifestWithSeparatePayloadAndResponseFiles()
    {
        using var store = new NativeClientSessionStore(_directory);
        var owner = Guid.NewGuid();
        var session = await store.ResolveAsync(owner, Identity(), 1, default);
        var request = new D { ["model"] = "test", ["input"] = "question" };
        var projection = await store.ProjectRequestAsync(session, request, "large-turn", default);
        var response = new D
        {
            ["id"] = "large-response",
            ["status"] = "completed",
            ["output"] = new List<object?>
            {
                new D { ["type"] = "message", ["content"] = new string('x', 2 * 1024 * 1024) }
            }
        };

        await store.CommitResponseAsync(session, projection, response, default);

        var records = Directory.GetFiles(_directory, "turn-*.json", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".payload.json", StringComparison.Ordinal)
                && !path.EndsWith(".response.json", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(records);
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(records[0])))
            Assert.Equal(2, manifest.RootElement.GetProperty("Version").GetInt32());
        Assert.NotEmpty(Directory.GetFiles(_directory, "turn-*.payload.json", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(_directory, "turn-*.response.json", SearchOption.AllDirectories));

        using var restarted = new NativeClientSessionStore(_directory);
        var restored = await restarted.TryGetAsync(owner, session.ThreadId, default);
        var replay = await restarted.ProjectRequestAsync(restored!, request, "large-turn", default);
        Assert.Equal("large-response", replay.ReplayResponse!["id"]);
    }

    [Fact]
    public async Task VersionOneRecordRemainsReplayable()
    {
        var owner = Guid.NewGuid();
        using (var store = new NativeClientSessionStore(_directory))
        {
            var session = await store.ResolveAsync(owner, Identity(), 1, default);
            var request = new D { ["model"] = "test", ["input"] = "legacy-question" };
            var projection = await store.ProjectRequestAsync(session, request, "legacy-turn", default);
            await store.CommitResponseAsync(session, projection, new D
            {
                ["id"] = "legacy-response",
                ["status"] = "completed",
                ["output"] = new List<object?>()
            }, default);
            var record = Assert.Single(
                Directory.GetFiles(_directory, "turn-*.json", SearchOption.AllDirectories),
                path => !path.EndsWith(".payload.json", StringComparison.Ordinal)
                    && !path.EndsWith(".response.json", StringComparison.Ordinal));
            var replayKey = Path.GetFileNameWithoutExtension(record)["turn-".Length..];
            var legacy = new D
            {
                ["Version"] = 1,
                ["Owner"] = owner,
                ["ThreadId"] = session.ThreadId,
                ["ReplayKey"] = replayKey,
                ["Payload"] = projection.Payload,
                ["Response"] = new D
                {
                    ["id"] = "legacy-response",
                    ["status"] = "completed",
                    ["output"] = new List<object?>()
                }
            };
            await File.WriteAllBytesAsync(record, JsonSerializer.SerializeToUtf8Bytes(legacy));
        }

        using var restarted = new NativeClientSessionStore(_directory);
        var restoredSession = await restarted.TryGetAsync(owner, "large-thread", default);
        var replay = await restarted.ProjectRequestAsync(restoredSession!,
            new D { ["model"] = "test", ["input"] = "legacy-question" }, "legacy-turn", default);
        Assert.Equal("legacy-response", replay.ReplayResponse!["id"]);
    }

    [Fact]
    public async Task ReplayReadsOnlyResponseAndDoesNotNeedTheHistoricalPayloadFile()
    {
        using var store = new NativeClientSessionStore(_directory);
        var owner = Guid.NewGuid();
        var session = await store.ResolveAsync(owner, Identity(), 1, default);
        var request = new D { ["model"] = "test", ["input"] = "question" };
        var projection = await store.ProjectRequestAsync(session, request, "replay-only", default);
        await store.CommitResponseAsync(session, projection, new D
        {
            ["id"] = "response-only",
            ["status"] = "completed",
            ["output"] = new List<object?>()
        }, default);
        var payloadFile = Assert.Single(
            Directory.GetFiles(_directory, "turn-*.payload.json", SearchOption.AllDirectories));
        File.Delete(payloadFile);

        var replay = await store.ProjectRequestAsync(session, request, "replay-only", default);

        Assert.Equal("response-only", replay.ReplayResponse!["id"]);
    }

    private static MultiAgentClientIdentity Identity() => new()
    {
        ThreadId = "large-thread",
        RootThreadId = "large-thread",
        AgentName = "/root"
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

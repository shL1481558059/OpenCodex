using OpenCodex.Core.Services.MultiAgent;
using System.Text.Json;
using Xunit;
using static OpenCodex.Api.Tests.MultiAgentTestHarness;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentRunStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ocxp-store-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SnapshotPreservesTaskBoundariesCompactionPrefixAndQueuedAssignments()
    {
        var store = new MultiAgentRunStore(_directory);
        var run = await store.GetAsync("owner/task-boundaries", null, Run, default);
        var root = run.Agents["/root"];
        root.TaskStarted = true;
        root.Status = "running";
        root.CurrentTaskGeneration = 2;
        root.Generation = 4;
        root.CurrentTurnStart = 1;
        root.CurrentTurnPrefix = [Message("BEFORE_COMPACTION", "user")];
        root.CompletedTurns = [new MultiAgentTaskTurn { Generation = 1, Status = "completed", Items = [Message("FINISHED_TASK", "user"), Message("RESULT")] }];
        root.PendingTasks =
        [
            new MultiAgentPendingTask { Generation = 3, Description = "NEXT", Messages = [Message("NEXT_TASK", "user")] },
            new MultiAgentPendingTask { Generation = 4, Description = "LATER", Messages = [Message("LATER_TASK", "user")] }
        ];
        run.LastResponseId = "task-boundary-response";
        await store.SaveAsync(run, default);
        var restored = (await new MultiAgentRunStore(_directory).GetAsync(run.SessionKey, run.LastResponseId, Run, default)).Agents["/root"];
        Assert.True(restored.TaskStarted);
        Assert.Equal("ready", restored.Status);
        Assert.Equal(2, restored.CurrentTaskGeneration);
        Assert.Equal(4, restored.Generation);
        Assert.Equal(1, restored.CurrentTurnStart);
        Assert.Equal(JsonSerializer.Serialize(root.CompletedTurns), JsonSerializer.Serialize(restored.CompletedTurns));
        Assert.Equal(JsonSerializer.Serialize(root.CurrentTurnPrefix), JsonSerializer.Serialize(restored.CurrentTurnPrefix));
        Assert.Equal(JsonSerializer.Serialize(root.PendingTasks), JsonSerializer.Serialize(restored.PendingTasks));
        Assert.IsType<Dictionary<string, object?>>(restored.CompletedTurns[0].Items[0]);
        Assert.IsType<Dictionary<string, object?>>(restored.PendingTasks[0].Messages[0]);
    }

    [Fact]
    public async Task MemoryOnlySaveResumesWithinProcessButNotAfterRestart()
    {
        var store = new MultiAgentRunStore(_directory);
        var run = await store.GetAsync("owner/session", null, Run, default);
        run.LastResponseId = "response-memory";
        await store.SaveAsync(run, default, persist: false);
        Assert.Same(run, await store.GetAsync("owner/session", "response-memory", Run, default));
        Assert.Same(store.Gate(run), store.Gate(run));
        Assert.False(Directory.Exists(_directory));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new MultiAgentRunStore(_directory).GetAsync("owner/session", "response-memory", Run, default));
    }

    [Fact]
    public async Task JsonRoundTripRestoresWeakTypesRoutingAndRunningState()
    {
        var store = new MultiAgentRunStore(_directory);
        var run = await store.GetAsync("api-key-a/session", null, Run, default);
        run.Template["nested"] = new Dictionary<string, object?> { ["items"] = new List<object?> { 3, 0.5, true, false, null, "text" } };
        run.Agents["/root"].Status = "running";
        run.Agents["/root"].Mailbox.Add(Message("mail", "user"));
        run.PendingCalls["call-pending"] = "/root";
        run.ReceivedCalls.Add("call-consumed");
        run.LastResponseId = "response-1";
        await store.SaveAsync(run, default);
        run.LastResponseId = "response-2";
        await store.SaveAsync(run, default);
        var restored = await new MultiAgentRunStore(_directory).GetAsync("api-key-a/session", "response-1", Run, default);
        Assert.NotSame(run, restored);
        Assert.Equal(run.Id, restored.Id);
        Assert.Equal("ready", restored.Agents["/root"].Status);
        Assert.Single(restored.Agents["/root"].Mailbox);
        Assert.Equal("/root", restored.PendingCalls["call-pending"]);
        Assert.Contains("call-consumed", restored.ReceivedCalls);
        Assert.Contains("response-2", restored.ResponseIds);
        var values = Assert.IsType<List<object?>>(Assert.IsType<Dictionary<string, object?>>(restored.Template["nested"])["items"]);
        Assert.Equal(3L, values[0]); Assert.Equal(0.5, values[1]); Assert.Equal(true, values[2]); Assert.Equal(false, values[3]); Assert.Null(values[4]); Assert.Equal("text", values[5]);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OwnerAndSessionKeysIsolatePreviousResponseLookup()
    {
        var store = new MultiAgentRunStore(_directory);
        var first = await store.GetAsync("api-key-a/session", null, Run, default);
        first.LastResponseId = "private-response";
        await store.SaveAsync(first, default);
        var other = await store.GetAsync("api-key-b/session", null, Run, default);
        Assert.NotSame(first, other);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetAsync("api-key-b/session", "private-response", Run, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetAsync("api-key-a/other-session", "private-response", Run, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new MultiAgentRunStore(_directory).GetAsync("api-key-b/session", "private-response", Run, default));
        Assert.DoesNotContain("api-key-a", string.Join(" ", Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories)));
    }

    [Fact]
    public async Task CancelledSaveDoesNotReplaceLastDurableSnapshot()
    {
        var store = new MultiAgentRunStore(_directory);
        var run = await store.GetAsync("owner/session", null, Run, default);
        run.LastResponseId = "saved";
        await store.SaveAsync(run, default);
        run.LastResponseId = "cancelled";
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(run, cancelled.Token));
        var restored = await new MultiAgentRunStore(_directory).GetAsync("owner/session", "saved", Run, default);
        Assert.Equal("saved", restored.LastResponseId);
        Assert.DoesNotContain("cancelled", restored.ResponseIds);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LatestRunAndHistoricalResponseResolveAfterRestart()
    {
        var store = new MultiAgentRunStore(_directory);
        var first = await store.GetAsync("owner/session", null, Run, default);
        first.LastResponseId = "first";
        await store.SaveAsync(first, default);
        var second = Run(); second.SessionKey = first.SessionKey; second.LastResponseId = "second";
        await store.SaveAsync(second, default);
        var reloaded = new MultiAgentRunStore(_directory);
        Assert.Equal(first.Id, (await reloaded.GetAsync(first.SessionKey, "first", Run, default)).Id);
        Assert.Equal(second.Id, (await reloaded.GetAsync(first.SessionKey, "second", Run, default)).Id);
        Assert.Equal(second.Id, (await reloaded.GetAsync(first.SessionKey, null, Run, default)).Id);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}

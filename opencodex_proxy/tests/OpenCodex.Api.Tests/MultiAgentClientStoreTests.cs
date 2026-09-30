using OpenCodex.Core.Services.MultiAgent;
using Xunit;
using static OpenCodex.Api.Tests.MultiAgentTestHarness;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentClientStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ocxp-client-store-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _owner = Guid.NewGuid();

    [Fact]
    public async Task ChildMayAttachBeforeSpawnResultAndDuplicateRequestsAreIdempotent()
    {
        var store = Store();
        var root = await Root(store);
        var child = await store.ReserveAsync(root, "spawn-1", "audit", Run(), true, default);
        Assert.Equal("/root/audit", child.AgentName);
        Assert.Equal("root-thread", child.ParentThreadId);
        Assert.Empty(child.ThreadId);
        var attached = await store.AttachAsync(_owner, root.RootThreadId, root.ThreadId, "child-thread", child.AgentName, "test", default);
        Assert.Same(child, attached);
        await store.CompleteSpawnAsync(root, "spawn-1", true, default);
        Assert.Same(child, await store.ReserveAsync(root, "spawn-1", "audit", Run(), true, default));
        Assert.Same(child, await store.AttachAsync(_owner, root.RootThreadId, root.ThreadId, "child-thread", child.AgentName, "test", default));
        Assert.Same(child, await store.TryGetAsync(_owner, root.RootThreadId, "child-thread", default));
        Assert.Same(root, await Root(store));
        Assert.NotSame(store.Gate(root), store.Gate(child));
    }

    [Fact]
    public async Task UnregisteredNamesWrongParentOwnerOrModelCannotClaimAChild()
    {
        var store = Store(); var root = await Root(store);
        await store.ReserveAsync(root, "spawn-1", "audit", Run(), true, default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.AttachAsync(Guid.NewGuid(), "root-thread", "root-thread", "child", "/root/audit", "test", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.AttachAsync(_owner, "root-thread", "wrong-parent", "child", "/root/audit", "test", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.AttachAsync(_owner, "root-thread", "root-thread", "child", "/root/not-reserved", "test", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AttachAsync(_owner, "root-thread", "root-thread", "child", "/root/audit", "another-model", default));
        Assert.Null(await store.TryGetAsync(Guid.NewGuid(), "root-thread", "root-thread", default));
    }

    [Fact]
    public async Task ASecondThreadOrDifferentCallCannotStealAnExistingAgent()
    {
        var store = Store(); var root = await Root(store);
        var child = await store.ReserveAsync(root, "spawn-1", "audit", Run(), true, default);
        await store.AttachAsync(_owner, "root-thread", "root-thread", "child", child.AgentName, "test", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AttachAsync(_owner, "root-thread", "root-thread", "other-child", child.AgentName, "test", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveAsync(root, "spawn-2", "audit", Run(), true, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveAsync(root, "spawn-1", "another-name", Run(), true, default));
    }

    [Fact]
    public async Task ReservationsRequireABoundParentAndOneTaskNameSegment()
    {
        var store = Store(); var root = await Root(store);
        var child = await store.ReserveAsync(root, "spawn-1", "audit", Run(), true, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveAsync(child, "nested", "data", Run(), true, default));
        foreach (var name in new[] { "", "..", "a/b", "/root/a" })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReserveAsync(root, "invalid-" + name, name, Run(), true, default));
    }

    [Fact]
    public async Task FailedSpawnCancelsAttachedActorAndCannotBeReattached()
    {
        var store = Store(); var root = await Root(store);
        var child = await store.ReserveAsync(root, "spawn-1", "audit", Run(), true, default);
        await store.AttachAsync(_owner, "root-thread", "root-thread", "child", child.AgentName, "test", default);
        await store.CompleteSpawnAsync(root, "spawn-1", false, default);
        Assert.True(child.SpawnFailed);
        Assert.True(child.Stop.IsCancellationRequested);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AttachAsync(_owner, "root-thread", "root-thread", "child", child.AgentName, "test", default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.AcquireModelAsync(child, default));
    }

    [Fact]
    public async Task PersistentGroupRestoresBindingsPendingReservationsAndIndependentActorHistory()
    {
        var store = Store(); var root = await Root(store);
        var child = await store.ReserveAsync(root, "spawn-1", "audit", Run(), true, default);
        child.Run.Agents["/root"].History.Add(Message("CHILD_PRIVATE_HISTORY"));
        await store.SaveAsync(child, default);
        await store.AttachAsync(_owner, "root-thread", "root-thread", "child", child.AgentName, "test", default);
        var pending = await store.ReserveAsync(child, "spawn-2", "data", Run(), true, default);
        var restoredStore = Store();
        var restoredRoot = await restoredStore.TryGetAsync(_owner, "root-thread", "root-thread", default);
        var restoredChild = await restoredStore.TryGetAsync(_owner, "root-thread", "child", default);
        Assert.NotNull(restoredRoot); Assert.NotNull(restoredChild);
        Assert.Equal(root.Id, restoredRoot.Id); Assert.Equal(child.Id, restoredChild.Id);
        Assert.Equal(2, restoredChild.Run.Agents["/root"].History.Count);
        Assert.Single(restoredRoot.Run.Agents["/root"].History);
        Assert.Single(restoredRoot.Run.Agents); Assert.Single(restoredChild.Run.Agents);
        Assert.Equal(pending.Id, (await restoredStore.AttachAsync(_owner, "root-thread", "child", "nested", "/root/audit/data", "test", default)).Id);
        var manifests = Directory.GetFiles(Path.Combine(_directory, "client-bindings"), "*.json");
        Assert.Single(manifests);
        Assert.DoesNotContain("CHILD_PRIVATE_HISTORY", await File.ReadAllTextAsync(manifests[0]));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MemoryOnlyRootDoesNotBecomeDurableWhenChildRequestsPersistence()
    {
        var store = Store(); var root = await Root(store, false);
        var child = await store.ReserveAsync(root, "spawn-1", "audit", Run(), true, default);
        Assert.False(child.Persist);
        await store.AttachAsync(_owner, "root-thread", "root-thread", "child", child.AgentName, "test", default);
        await store.SaveAsync(child, default);
        Assert.False(Directory.Exists(_directory));
        Assert.Null(await Store().TryGetAsync(_owner, "root-thread", "child", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store().AttachAsync(_owner, "root-thread", "root-thread", "child", child.AgentName, "test", default));
    }

    [Fact]
    public async Task RootDoesNotConsumeTheChildModelLimitAndWaitingDoesNotHoldMetadataLock()
    {
        var store = Store();
        var root = await store.OpenRootAsync(_owner, "root-thread", () => { var run = Run(); run.MaxConcurrentSubagents = 1; return run; }, false, default);
        var first = await store.ReserveAsync(root, "spawn-1", "one", Run(), false, default);
        var second = await store.ReserveAsync(root, "spawn-2", "two", Run(), false, default);
        await using var rootLease = await store.AcquireModelAsync(root, default);
        var firstLease = await store.AcquireModelAsync(first, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = store.AcquireModelAsync(second, timeout.Token);
        Assert.False(waiting.IsCompleted);
        Assert.Same(root, await store.TryGetAsync(_owner, "root-thread", "root-thread", timeout.Token));
        await store.ReserveAsync(root, "spawn-3", "three", Run(), false, timeout.Token);
        await firstLease.DisposeAsync();
        await using var secondLease = await waiting;
    }

    [Fact]
    public async Task FailedSpawnReleasesPendingWaitWithoutLeakingModelPermit()
    {
        var store = Store();
        var root = await store.OpenRootAsync(_owner, "root-thread", () => { var run = Run(); run.MaxConcurrentSubagents = 1; return run; }, false, default);
        var first = await store.ReserveAsync(root, "spawn-1", "one", Run(), false, default);
        var second = await store.ReserveAsync(root, "spawn-2", "two", Run(), false, default);
        var lease = await store.AcquireModelAsync(first, default);
        var waiting = store.AcquireModelAsync(second, default);
        await store.CompleteSpawnAsync(root, "spawn-2", false, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        await lease.DisposeAsync(); await lease.DisposeAsync();
        await using var nextLease = await store.AcquireModelAsync(first, default);
    }

    [Fact]
    public async Task OrdinaryToolCompletionHasNoSpawnSideEffects()
    {
        var store = Store(); var root = await Root(store, false);
        await store.CompleteSpawnAsync(root, "exec-call", true, default);
        await store.CompleteSpawnAsync(root, "exec-call", false, default);
        Assert.False(root.SpawnFailed);
        Assert.False(root.Stop.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellingOneActivityLeavesSiblingAndSubsequentActivityRunning()
    {
        var store = Store(); var root = await Root(store, false);
        var child = await store.ReserveAsync(root, "spawn", "audit", Run(), false, default);
        using var rootActivity = store.BeginActivity(root, default);
        var activity = store.BeginActivity(child, default);
        Assert.Throws<InvalidOperationException>(() => store.BeginActivity(child, default));
        await store.CancelActivityAsync(child);
        Assert.True(activity.Token.IsCancellationRequested);
        Assert.False(rootActivity.Token.IsCancellationRequested);
        Assert.False(child.Stop.IsCancellationRequested);
        activity.Dispose();
        using var next = store.BeginActivity(child, default);
        activity.Dispose();
        Assert.False(next.Token.IsCancellationRequested);
        await store.CancelActivityAsync(child);
        Assert.True(next.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task RequestAbortAndSpawnFailureCancelRegisteredActivity()
    {
        var store = Store(); var root = await Root(store, false);
        var child = await store.ReserveAsync(root, "spawn", "audit", Run(), false, default);
        using var aborted = new CancellationTokenSource();
        using (var request = store.BeginActivity(child, aborted.Token))
        {
            await aborted.CancelAsync();
            Assert.True(request.Token.IsCancellationRequested);
        }
        using var next = store.BeginActivity(child, default);
        await store.CompleteSpawnAsync(root, "spawn", false, default);
        Assert.True(next.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task MissingDurableActorSnapshotFailsRecoveryInsteadOfCreatingAnotherAgent()
    {
        var store = Store(); var root = await Root(store);
        await store.ReserveAsync(root, "spawn", "audit", Run(), true, default);
        Directory.Delete(Path.Combine(_directory, "runs"), true);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().TryGetAsync(_owner, "root-thread", "root-thread", default));
    }

    [Fact]
    public async Task AgentLookupResolvesRelativeAndAbsoluteNamesInsideOnlyTheCallersGroup()
    {
        var store = Store(); var root = await Root(store, false);
        var child = await store.ReserveAsync(root, "spawn", "audit", Run(), false, default);
        await store.AttachAsync(_owner, root.RootThreadId, root.ThreadId, "child", child.AgentName, "test", default);
        var nested = await store.ReserveAsync(child, "nested", "data", Run(), false, default);
        var otherRoot = await store.OpenRootAsync(_owner, "other-root", Run, false, default);
        var otherChild = await store.ReserveAsync(otherRoot, "spawn", "audit", Run(), false, default);
        var anotherOwner = await store.OpenRootAsync(Guid.NewGuid(), "root-thread", Run, false, default);
        var privateChild = await store.ReserveAsync(anotherOwner, "spawn", "private", Run(), false, default);
        Assert.Same(child, await store.TryGetAgentAsync(root, "audit", default));
        Assert.Same(child, await store.TryGetAgentAsync(child, "/root/audit", default));
        Assert.Same(nested, await store.TryGetAgentAsync(child, "data", default));
        Assert.Same(root, await store.TryGetAgentAsync(nested, "/root", default));
        Assert.Same(otherChild, await store.TryGetAgentAsync(otherRoot, "audit", default));
        Assert.Same(privateChild, await store.TryGetAgentAsync(anotherOwner, "private", default));
        Assert.Null(await store.TryGetAgentAsync(root, "/root/private", default));
        Assert.Null(await store.TryGetAgentAsync(child, "../audit", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().TryGetAgentAsync(root, "audit", default));
    }

    [Fact]
    public async Task PendingSeedWithoutAResponseIdCanBeReadAndAttachedAfterRestart()
    {
        var store = Store(); var root = await Root(store);
        var seed = Run(); seed.Agents["/root"].History.Add(Message("FORKED_PARENT_HISTORY"));
        var reserved = await store.ReserveAsync(root, "spawn", "audit", seed, true, default);
        Assert.Empty(reserved.Run.LastResponseId);
        Assert.Empty(reserved.Run.ResponseIds);
        var restored = Store();
        var restoredRoot = await restored.OpenRootAsync(_owner, "root-thread", () => throw new InvalidOperationException("Do not create a new root."), true, default);
        var pending = await restored.TryGetAgentAsync(restoredRoot, "audit", default);
        Assert.NotNull(pending);
        Assert.Equal(reserved.Id, pending.Id);
        Assert.Empty(pending.ThreadId);
        Assert.Equal("root-thread", pending.RootThreadId);
        Assert.Equal(2, pending.Run.Agents["/root"].History.Count);
        Assert.Same(pending, await restored.AttachAsync(_owner, "root-thread", "root-thread", "child", pending.AgentName, "test", default));
    }

    [Fact]
    public async Task AgentLookupDoesNotReturnAFailedReservationAfterRetry()
    {
        var store = Store(); var root = await Root(store, false);
        var failed = await store.ReserveAsync(root, "first-spawn", "audit", Run(), false, default);
        await store.CompleteSpawnAsync(root, "first-spawn", false, default);
        Assert.Null(await store.TryGetAgentAsync(root, "audit", default));
        var retry = await store.ReserveAsync(root, "retry-spawn", "audit", Run(), false, default);
        Assert.NotSame(failed, retry);
        Assert.Same(retry, await store.TryGetAgentAsync(root, "audit", default));
    }

    private MultiAgentClientStore Store() => new(new MultiAgentRunStore(Path.Combine(_directory, "runs")), _directory);
    private Task<MultiAgentClientBinding> Root(MultiAgentClientStore store, bool persist = true) =>
        store.OpenRootAsync(_owner, "root-thread", Run, persist, default);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

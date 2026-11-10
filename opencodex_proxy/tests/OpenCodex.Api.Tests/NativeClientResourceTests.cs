using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D=System.Collections.Generic.Dictionary<string,object?>;

namespace OpenCodex.Api.Tests;

public sealed class NativeClientResourceTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"native-resources-"+Guid.NewGuid().ToString("N"));
    private readonly Guid _owner=Guid.NewGuid();
    private readonly Clock _clock=new();
    private NativeClientSessionStore Store(NativeClientCacheOptions? options=null)=>new(_directory,options??new(){CleanupIntervalSeconds=3600},_clock);
    private Task<NativeClientSession> Session(NativeClientSessionStore store,string name="root",Guid? owner=null,bool persist=false)=>store.ResolveAsync(owner??_owner,new(){ThreadId=name,RootThreadId=name,AgentName="/root"},persist,1,default);
    private static D Request(string text="question")=>new(){["model"]="test",["input"]=text};
    private static D Response(string id,string text="answer")=>new(){["id"]=id,["status"]="completed",["output"]=new List<object?>{new D{["type"]="message",["role"]="assistant",["content"]=new List<object?>{new D{["type"]="output_text",["text"]=text}}}}};
    private static async Task Commit(NativeClientSessionStore store,NativeClientSession session,string turn,string id)
    {var request=await store.ProjectRequestAsync(session,Request(),turn,default);await store.CommitResponseAsync(session,request,Response(id),default);}

    [Fact]
    public async Task EvictedReplayFailsClosedButNewTurnsAndRetainedReplayStillWork()
    {
        using var store=Store(new(){MaxResponses=1,CleanupIntervalSeconds=3600});var session=await Session(store);
        await Commit(store,session,"old","r1");_clock.Advance(1);await Commit(store,session,"new","r2");
        var error=await Assert.ThrowsAsync<ProxyException>(()=>store.ProjectRequestAsync(session,Request(),"old",default));Assert.Equal(409,error.StatusCode);
        Assert.Equal("r2",(await store.ProjectRequestAsync(session,Request(),"new",default)).ReplayResponse!["id"]);
        Assert.Null((await store.ProjectRequestAsync(session,Request(),"brand-new",default)).ReplayResponse);
        var stats=await store.ResourceUsageAsync();Assert.Equal(1,stats.Responses);Assert.Equal(1,stats.Replays);Assert.Equal(2,stats.ReplayIdentities);
        Assert.False(Directory.Exists(_directory));
    }
    [Fact]
    public async Task EvictedExecutableTurnNeverCallsModelOrPublishesToolAgain()
    {
        using var store=Store(new(){MaxResponses=1,CleanupIntervalSeconds=3600});var session=await Session(store);
        var projection=await store.ProjectRequestAsync(session,Request(),"old",default);
        var response=Response("r1");response["output"]=new List<object?>{new D{["type"]="function_call",["name"]="exec",["call_id"]="side-effect-once",["arguments"]="{}"}};
        await store.CommitResponseAsync(session,projection,response,default);await Commit(store,session,"new","r2");
        var calls=0;var emitted=0;var executor=new NativeClientResponseExecutor(store);
        var error=await Assert.ThrowsAsync<ProxyException>(()=>executor.ExecuteAsync(session,Request(),"old",(payload,onEvent,ct)=>{calls++;return Task.FromResult(Response("unexpected"));},item=>{emitted++;return Task.CompletedTask;},default));
        Assert.Equal(409,error.StatusCode);Assert.Equal(0,calls);Assert.Equal(0,emitted);Assert.Equal(0,(await store.ResourceUsageAsync()).References);
    }
    [Fact]
    public async Task SnapshotTtlRemovesAllAliasesAndInternPoolWithoutBlockingGenuinelyNewTurn()
    {
        using var store=Store(new(){SnapshotTtlSeconds=1,SessionTtlSeconds=100,CleanupIntervalSeconds=3600});var session=await Session(store);
        await Commit(store,session,"a","same-id");await Commit(store,session,"b","same-id");
        Assert.Equal(2,(await store.ResourceUsageAsync()).Replays);
        _clock.Advance(2);await store.CleanupAsync();var stats=await store.ResourceUsageAsync();
        Assert.Equal(0,stats.Responses);Assert.Equal(0,stats.Replays);Assert.Equal(0,stats.Pools);Assert.Equal(0,stats.Bytes);
        foreach(var turn in new[]{"a","b"})Assert.Equal(409,(await Assert.ThrowsAsync<ProxyException>(()=>store.ProjectRequestAsync(session,Request(),turn,default))).StatusCode);
        await Commit(store,session,"c","r3");Assert.Equal(1,(await store.ResourceUsageAsync()).Responses);
    }
    [Fact]
    public async Task TtlReclaimsWholeInactiveGroupButNotOwnerOrRegisteredWaiter()
    {
        using var store=Store(new(){SessionTtlSeconds=1,CleanupIntervalSeconds=3600});var session=await Session(store);
        var owner=await store.AcquireRequestAsync(session,default);var waiting=store.AcquireRequestAsync(session,default);
        _clock.Advance(10);await store.CleanupAsync();Assert.Equal(2,(await store.ResourceUsageAsync()).References);
        await owner.DisposeAsync();var next=await waiting;await store.CleanupAsync();Assert.Equal(1,(await store.ResourceUsageAsync()).Sessions);
        await next.DisposeAsync();_clock.Advance(2);await store.CleanupAsync();var stats=await store.ResourceUsageAsync();
        Assert.Equal(0,stats.Sessions);Assert.Equal(0,stats.Groups);Assert.Equal(0,stats.References);
        Assert.Equal(409,(await Assert.ThrowsAsync<ProxyException>(()=>Session(store))).StatusCode);
        Assert.Equal(409,(await Assert.ThrowsAsync<ProxyException>(()=>store.AcquireRequestAsync(session,default))).StatusCode);
        Assert.False(Directory.Exists(_directory));
    }
    [Fact]
    public async Task CancelledWaiterReferenceIsReleasedAndAdmissionCannotCreateSecondLock()
    {
        using var store=Store(new(){MaxWaitersPerSession=1,SessionTtlSeconds=1,CleanupIntervalSeconds=3600});var session=await Session(store);
        for(var iteration=0;iteration<100;iteration++)
        {
            var owner=await store.AcquireRequestAsync(session,default);using var stop=new CancellationTokenSource();
            var waiting=store.AcquireRequestAsync(session,stop.Token);
            Assert.Equal(503,(await Assert.ThrowsAsync<ProxyException>(()=>store.AcquireRequestAsync(session,default))).StatusCode);
            _clock.Advance(10);await store.CleanupAsync();Assert.Same(session,await Session(store));
            stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiting);
            Assert.Equal(1,(await store.ResourceUsageAsync()).References);await owner.DisposeAsync();
            Assert.Equal(0,(await store.ResourceUsageAsync()).References);
        }
    }
    [Fact]
    public async Task PerTenantPressureEvictsOnlyThatTenantsSnapshotsAndCannotCrossOwner()
    {
        using var store=Store(new(){MaxTenantResponses=1,CleanupIntervalSeconds=3600});var other=Guid.NewGuid();
        var a=await Session(store);var b=await Session(store,owner:other);
        await Commit(store,b,"b1","b-r1");await Commit(store,a,"a1","a-r1");await Commit(store,a,"a2","a-r2");
        Assert.Equal("b-r1",(await store.ProjectRequestAsync(b,Request(),"b1",default)).ReplayResponse!["id"]);
        Assert.Equal(409,(await Assert.ThrowsAsync<ProxyException>(()=>store.ProjectRequestAsync(a,Request(),"a1",default))).StatusCode);
        Assert.Null((await store.ProjectRequestAsync(a,Request(),"b1",default)).ReplayResponse);
    }
    [Fact]
    public async Task PersistentGroupsLazyRestoreAfterTtlAndRespectBoundedRegistry()
    {
        using var store=Store(new(){MaxSessions=2,MaxTenantSessions=2,SessionTtlSeconds=1,CleanupIntervalSeconds=3600});
        for(var i=0;i<6;i++){var session=await Session(store,"t"+i,persist:true);await Commit(store,session,"turn","r"+i);Assert.InRange((await store.ResourceUsageAsync()).Sessions,1,2);}
        _clock.Advance(2);await store.CleanupAsync();Assert.Equal(0,(await store.ResourceUsageAsync()).Sessions);
        var restored=await Session(store,"t0",persist:true);Assert.Equal("r0",(await store.ProjectRequestAsync(restored,Request(),"turn",default)).ReplayResponse!["id"]);
        Assert.Equal(0,(await store.ResourceUsageAsync()).SafetyIdentities);
    }
    [Fact]
    public async Task ChurnReleasesPayloadsPoolsGatesButRetainsBoundedSafetyTombstones()
    {
        using var store=Store(new(){SessionTtlSeconds=1,MaxSessions=4,MaxTenantSessions=4,MaxEphemeralIdentities=600,MaxTenantEphemeralIdentities=600,CleanupIntervalSeconds=3600});
        for(var i=0;i<500;i++)
        {
            var session=await Session(store,"t"+i);await using(var lease=await store.AcquireRequestAsync(session,default))await Commit(store,session,"turn","r"+i);
            _clock.Advance(2);await store.CleanupAsync();
            var stats=await store.ResourceUsageAsync();Assert.Equal(0,stats.Sessions);Assert.Equal(0,stats.Groups);Assert.Equal(0,stats.Pools);Assert.Equal(0,stats.Responses);Assert.Equal(0,stats.Replays);Assert.Equal(0,stats.Bytes);Assert.Equal(0,stats.ReplayIdentities);
        }
        Assert.Equal(500,(await store.ResourceUsageAsync()).SafetyIdentities);Assert.False(Directory.Exists(_directory));
    }
    [Fact]
    public async Task SafetyAdmissionIsFiniteAndFailsBeforeAcceptingNewReplayIdentity()
    {
        using var store=Store(new(){MaxTenantEphemeralIdentities=2,MaxEphemeralIdentities=3,SessionTtlSeconds=1,CleanupIntervalSeconds=3600});
        await Session(store,"one");await Session(store,"two");_clock.Advance(2);await store.CleanupAsync();
        Assert.Equal(503,(await Assert.ThrowsAsync<ProxyException>(()=>Session(store,"three"))).StatusCode);
        await Session(store,"one",Guid.NewGuid());Assert.Equal(3,(await store.ResourceUsageAsync()).SafetyIdentities);
        Assert.Equal(409,(await Assert.ThrowsAsync<ProxyException>(()=>Session(store,"one",persist:true))).StatusCode);
    }
    [Fact]
    public async Task ReplayKeyBudgetFailureDoesNotPublishOrLoseExistingReplay()
    {
        using var store=Store(new(){MaxSessionReplayIdentities=1,CleanupIntervalSeconds=3600});var session=await Session(store);
        await Commit(store,session,"a","r1");
        Assert.Equal(503,(await Assert.ThrowsAsync<ProxyException>(()=>store.ProjectRequestAsync(session,Request(),"b",default))).StatusCode);
        Assert.Equal("r1",(await store.ProjectRequestAsync(session,Request(),"a",default)).ReplayResponse!["id"]);
        Assert.Equal(1,(await store.ResourceUsageAsync()).Responses);
    }
    [Fact]
    public async Task OversizeSnapshotIsRejectedBeforeCopyInternAndCommit()
    {
        using var store=Store(new(){MaxSnapshotBytes=1024,CleanupIntervalSeconds=3600});var session=await Session(store);
        var projection=await store.ProjectRequestAsync(session,Request(new string('x',10_000)),"turn",default);
        Assert.Equal(503,(await Assert.ThrowsAsync<ProxyException>(()=>store.CommitResponseAsync(session,projection,Response("r"),default))).StatusCode);
        var stats=await store.ResourceUsageAsync();Assert.Equal(0,stats.Bytes);Assert.Equal(0,stats.Pools);Assert.Equal(0,stats.ReplayIdentities);
    }
    [Fact]
    public async Task StoreDisposeKeepsActiveGateAliveUntilFinalLeaseAndThenReclaims()
    {
        var store=Store();var session=await Session(store);var lease=await store.AcquireRequestAsync(session,default);
        store.Dispose();Assert.Equal(1,(await store.ResourceUsageAsync()).References);
        await lease.DisposeAsync();Assert.Equal(0,(await store.ResourceUsageAsync()).Sessions);
    }
    [Fact]
    public async Task ChildPermitWaiterPinsEntireGroupUntilCancellationAndFinalRelease()
    {
        using var store=Store(new(){SessionTtlSeconds=1,CleanupIntervalSeconds=3600});await Session(store);
        Task<NativeClientSession> Child(string name)=>store.ResolveAsync(_owner,new(){ThreadId=name,RootThreadId="root",ParentThreadId="root",AgentName="/root/"+name},false,1,default);
        var a=await Child("a");var b=await Child("b");var owner=await store.AcquireRequestAsync(a,default);
        using var cancel=new CancellationTokenSource();var waiting=store.AcquireRequestAsync(b,cancel.Token);
        _clock.Advance(10);await store.CleanupAsync();var active=await store.ResourceUsageAsync();Assert.Equal(3,active.Sessions);Assert.Equal(2,active.References);
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiting);await owner.DisposeAsync();
        _clock.Advance(2);await store.CleanupAsync();Assert.Equal(0,(await store.ResourceUsageAsync()).Groups);
    }
    [Fact]
    public async Task ActiveGroupAdmissionFailureDoesNotConsumeSafetyIdentityOrReplaceGate()
    {
        using var store=Store(new(){MaxSessions=1,MaxTenantSessions=1,CleanupIntervalSeconds=3600});var root=await Session(store);
        await using var lease=await store.AcquireRequestAsync(root,default);
        Assert.Equal(503,(await Assert.ThrowsAsync<ProxyException>(()=>Session(store,"new"))).StatusCode);
        Assert.Same(root,await Session(store));Assert.Equal(1,(await store.ResourceUsageAsync()).SafetyIdentities);
    }

    [Fact]
    public async Task ConcurrentWaitCancellationAndCleanupNeverAdmitTwoOwnersAndReleaseEveryReference()
    {
        using var store=Store(new(){MaxWaitersPerSession=1000,SessionTtlSeconds=1,CleanupIntervalSeconds=3600});var session=await Session(store);
        var active=0;var maximum=0;var success=0;var cancelled=0;
        await Task.WhenAll(Enumerable.Range(0,500).Select(index=>Task.Run(async()=>
        {
            using var stop=new CancellationTokenSource();if(index%3==0)stop.Cancel();
            try
            {
                await using var lease=await store.AcquireRequestAsync(session,stop.Token);
                var current=Interlocked.Increment(ref active);Interlocked.Exchange(ref maximum,Math.Max(Volatile.Read(ref maximum),current));
                await store.CleanupAsync();await Task.Yield();Interlocked.Decrement(ref active);Interlocked.Increment(ref success);
            }
            catch(OperationCanceledException){Interlocked.Increment(ref cancelled);}
        })));
        Assert.Equal(1,maximum);Assert.True(success>0);Assert.True(cancelled>0);Assert.Equal(0,(await store.ResourceUsageAsync()).References);
        _clock.Advance(2);await store.CleanupAsync();Assert.Equal(0,(await store.ResourceUsageAsync()).Sessions);
    }

    [Fact]
    public async Task FailedDurableIdentityWriteDoesNotLeakGroupsOrGates()
    {
        await File.WriteAllTextAsync(_directory,"not a directory");
        try
        {
            using var store=Store();
            for(var i=0;i<20;i++)await Assert.ThrowsAnyAsync<IOException>(()=>Session(store,"failed"+i,persist:true));
            var stats=await store.ResourceUsageAsync();Assert.Equal(0,stats.Groups);Assert.Equal(0,stats.Sessions);Assert.Equal(0,stats.SafetyIdentities);
        }
        finally{File.Delete(_directory);}
    }
    [Fact]
    public async Task PersistentTtlLazyRestoreCannotRebindExistingAgentName()
    {
        using var store=Store(new(){SessionTtlSeconds=1,CleanupIntervalSeconds=3600});await Session(store,persist:true);
        await store.ResolveAsync(_owner,new(){ThreadId="old-child",RootThreadId="root",ParentThreadId="root",AgentName="/root/audit"},true,1,default);
        _clock.Advance(2);
        // No explicit Cleanup: Resolve itself must evict then restore the FULL group.
        await Assert.ThrowsAsync<BadRequestException>(()=>store.ResolveAsync(_owner,new(){ThreadId="new-child",RootThreadId="root",ParentThreadId="root",AgentName="/root/audit"},true,1,default));
        Assert.NotNull(await store.TryGetAsync(_owner,"old-child",default));
    }

    private sealed class Clock:TimeProvider
    {
        private long _ticks=DateTimeOffset.Parse("2026-01-01T00:00:00Z").UtcTicks;
        public override DateTimeOffset GetUtcNow()=>new(Interlocked.Read(ref _ticks),TimeSpan.Zero);
        public void Advance(int seconds)=>Interlocked.Add(ref _ticks,TimeSpan.FromSeconds(seconds).Ticks);
    }
    public void Dispose(){if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

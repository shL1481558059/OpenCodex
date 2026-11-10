using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using Xunit;
namespace OpenCodex.Api.Tests;
public sealed class LegacyRuntimeResourceTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"ocx-runtime-"+Guid.NewGuid().ToString("N"));
    private readonly Clock _clock=new();
    private readonly Guid _owner=Guid.NewGuid();
    private static MultiAgentRun Run()=>new(){Model="synthetic",Agents=new(){["/root"]=new(){Name="/root"}}};
    private MultiAgentRunStore Store(MultiAgentRunCacheOptions? options=null)=>new(_directory,options??new(){TtlSeconds=1},_clock);
    [Fact]
    public async Task ActiveAndRegisteredWaiterPreventEvictionThenIdleReclaimsWithoutReplay()
    {
        using var store=Store(new(){TtlSeconds=1,MaxSessions=1,MaxTenantSessions=1});var run=await store.GetAsync("session",null,Run,default,_owner);
        var active=await store.AcquireRequestAsync(run,default);using var stop=new CancellationTokenSource();var waiting=store.AcquireRequestAsync(run,stop.Token);
        _clock.Advance(2);await store.CleanupAsync();Assert.Equal(1,store.Usage.Runs);
        await Assert.ThrowsAsync<ProxyException>(()=>store.GetAsync("other",null,Run,default,_owner));
        stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiting);await active.DisposeAsync();
        _clock.Advance(2);await store.CleanupAsync();Assert.Equal(0,store.Usage.Runs);Assert.Equal(0,store.Usage.Bytes);
        var error=await Assert.ThrowsAsync<ProxyException>(()=>store.GetAsync("session",null,Run,default,_owner));Assert.Equal(409,error.StatusCode);
    }
    [Fact]
    public async Task PersistedEvictionRestoresButMissingDurableFileNeverCreatesNewRuntime()
    {
        using var store=Store();var run=await store.GetAsync("persistent",null,Run,default,_owner);
        await using(var lease=await store.AcquireRequestAsync(run,default)){run.LastResponseId="complete";await store.SaveAsync(run,default);}
        _clock.Advance(2);await store.CleanupAsync();Assert.Equal(0,store.Usage.Runs);
        var restored=await store.GetAsync("persistent","complete",Run,default,_owner);Assert.NotSame(run,restored);Assert.Equal(run.Id,restored.Id);
        _clock.Advance(2);await store.CleanupAsync();Directory.Delete(_directory,true);
        var error=await Assert.ThrowsAsync<ProxyException>(()=>store.GetAsync("persistent",null,()=>throw new Exception("must not recreate"),default,_owner));Assert.Equal(409,error.StatusCode);
    }
    [Fact]
    public async Task DirtyRuntimeAndRawCompatibilityGateAreNotSilentlyRestoredOrReplaced()
    {
        using var store=Store(new(){TtlSeconds=1,MaxSessions=1});var run=await store.GetAsync("raw",null,Run,default,_owner);var gate=store.Gate(run);
        _clock.Advance(2);await store.CleanupAsync();Assert.Same(gate,store.Gate(run));
        await Assert.ThrowsAsync<ProxyException>(()=>store.GetAsync("blocked",null,Run,default,_owner));
    }
    [Fact]
    public async Task TenantSessionAdmissionDoesNotEvictOtherOwnersActiveRuntime()
    {
        using var store=Store(new(){MaxTenantSessions=1});var run=await store.GetAsync("a",null,Run,default,_owner);
        await using var active=await store.AcquireRequestAsync(run,default);
        await store.GetAsync("b",null,Run,default,Guid.NewGuid());Assert.Equal(2,store.Usage.Sessions);
        var error=await Assert.ThrowsAsync<ProxyException>(()=>store.GetAsync("c",null,Run,default,_owner));Assert.Equal(503,error.StatusCode);
    }
    [Fact]
    public async Task OversizeInitialAndDirtySnapshotsFailExplicitlyAndDirtyGraphReleasesAfterLease()
    {
        using var store=Store(new(){MaxRunBytes=4000});
        await Assert.ThrowsAsync<ProxyException>(()=>store.GetAsync("oversize",null,()=>new(){Model=new string('x',10000)},default,_owner));Assert.Equal(0,store.Usage.Runs);
        var run=await store.GetAsync("dirty",null,Run,default,_owner);var lease=await store.AcquireRequestAsync(run,default);run.Model=new string('x',10000);
        await Assert.ThrowsAsync<ProxyException>(()=>store.SaveAsync(run,default,false));await lease.DisposeAsync();Assert.Equal(0,store.Usage.Runs);Assert.Equal(0,store.Usage.Bytes);
    }
    [Fact]
    public async Task BindingChurnReleasesRuntimeRetentionsAndRejectsOldMemoryOnlyRoot()
    {
        using var runs=Store();using var bindings=new MultiAgentClientStore(runs,_directory,new(){TtlSeconds=1},_clock);
        for(var i=0;i<100;i++)
        {
            await bindings.OpenRootAsync(_owner,"root"+i,Run,false,default);_clock.Advance(2);await bindings.CleanupAsync();
            _clock.Advance(2);await runs.CleanupAsync();Assert.Equal(0,bindings.CachedGroups);Assert.Equal(0,runs.Usage.Runs);
        }
        var error=await Assert.ThrowsAsync<ProxyException>(()=>bindings.OpenRootAsync(_owner,"root0",()=>throw new Exception("must not replay"),false,default));Assert.Equal(409,error.StatusCode);
    }
    [Fact]
    public async Task BindingActivityAndChildPermitWaiterProtectWholeGroupAndCancelReleases()
    {
        using var runs=Store();using var bindings=new MultiAgentClientStore(runs,_directory,new(){TtlSeconds=1},_clock);
        var root=await bindings.OpenRootAsync(_owner,"root",()=>{var run=Run();run.MaxConcurrentSubagents=1;return run;},false,default);
        var a=await bindings.ReserveAsync(root,"a","a",Run(),false,default);var b=await bindings.ReserveAsync(root,"b","b",Run(),false,default);
        using var activity=bindings.BeginActivity(root,default);var first=await bindings.AcquireModelAsync(a,default);using var stop=new CancellationTokenSource();var waiting=bindings.AcquireModelAsync(b,stop.Token);
        _clock.Advance(2);await bindings.CleanupAsync();Assert.Equal(1,bindings.CachedGroups);
        stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiting);
        await Task.WhenAll(Task.Run(async()=>await first.DisposeAsync()),Task.Run(async()=>await first.DisposeAsync()));
        activity.Dispose();_clock.Advance(2);await bindings.CleanupAsync();Assert.Equal(0,bindings.CachedGroups);
    }
    [Fact]
    public async Task RuntimeWaitCancelAndSweepChurnNeverAllowsTwoOwners()
    {
        using var store=Store();var run=await store.GetAsync("race",null,Run,default,_owner);var owners=0;
        await Task.WhenAll(Enumerable.Range(0,100).Select(async i=>
        {
            using var stop=new CancellationTokenSource();if(i%3==0)stop.Cancel();
            try{await using var lease=await store.AcquireRequestAsync(run,stop.Token);Assert.Equal(1,Interlocked.Increment(ref owners));await Task.Yield();Interlocked.Decrement(ref owners);}
            catch(OperationCanceledException){}
        }));Assert.Equal(0,owners);
    }
    [Fact]
    public async Task OversizeBindingDoesNotPinDirtyGraphUntilTtlOrReplayOldSnapshot()
    {
        using var runs=Store(new(){MaxRunBytes=4000});using var bindings=new MultiAgentClientStore(runs,_directory);
        var root=await bindings.OpenRootAsync(_owner,"dirty-binding",Run,false,default);using var activity=bindings.BeginActivity(root,default);
        root.Run.Model=new string('x',10000);await Assert.ThrowsAsync<ProxyException>(()=>bindings.SaveAsync(root,default));Assert.Equal(1,bindings.CachedGroups);
        activity.Dispose();Assert.Equal(0,bindings.CachedGroups);Assert.Equal(0,runs.Usage.Runs);Assert.Equal(0,runs.Usage.Bytes);
        await Assert.ThrowsAsync<ProxyException>(()=>bindings.OpenRootAsync(_owner,"dirty-binding",()=>throw new Exception("must not replay"),false,default));
    }
    public void Dispose(){if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
    private sealed class Clock:TimeProvider{private DateTimeOffset _now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>_now;internal void Advance(int seconds)=>_now+=TimeSpan.FromSeconds(seconds);}
}

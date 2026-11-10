using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.Proxy;
using Xunit;
using D=System.Collections.Generic.Dictionary<string,object?>;
namespace OpenCodex.Api.Tests;

public sealed class ResponsesContextGovernanceTests
{
    [Fact]
    public async Task CancelledWaiterNeverCreatesSecondLockAndChurnReleasesAllKeys()
    {
        var locks=new RefCountedKeyedLock(32,16);
        for(var iteration=0;iteration<1000;iteration++)
        {
            var owner=await locks.AcquireAsync("key",default);
            using var cancellation=new CancellationTokenSource();
            var cancelled=locks.AcquireAsync("key",cancellation.Token).AsTask();
            var next=locks.AcquireAsync("key",default).AsTask();
            cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled);
            Assert.False(next.IsCompleted);owner.Dispose();owner.Dispose();
            using(await next) { Assert.Equal(1,locks.Count); }
            Assert.Equal(0,locks.Count);
        }
        await Parallel.ForEachAsync(Enumerable.Range(0,10000),new ParallelOptions { MaxDegreeOfParallelism=16 },async (id,ct)=>
        { using var lease=await locks.AcquireAsync(id.ToString(),ct);await Task.Yield(); });
        Assert.Equal(0,locks.Count);
    }
    [Fact]
    public async Task ConcurrentSameKeyAndCancellationAreSerializedWithoutLeakedWaiters()
    {
        var locks=new RefCountedKeyedLock(8,1024);var inside=0;var maxInside=0;
        await Task.WhenAll(Enumerable.Range(0,500).Select(async i=>
        {
            using var ct=new CancellationTokenSource();var acquiring=locks.AcquireAsync("same",ct.Token);
            if(i%3==0)ct.Cancel();
            try { using var lease=await acquiring;var n=Interlocked.Increment(ref inside);maxInside=Math.Max(maxInside,n);await Task.Yield();Interlocked.Decrement(ref inside); }
            catch(OperationCanceledException) { }
        }));
        Assert.Equal(1,maxInside);Assert.Equal(0,locks.Count);
    }
    [Fact]
    public async Task LockAndWaiterQuotasFailExplicitlyAndCancelReclaimsQuota()
    {
        var locks=new RefCountedKeyedLock(1,1);using var owner=await locks.AcquireAsync("a",default);
        using var ct=new CancellationTokenSource();var waiting=locks.AcquireAsync("a",ct.Token).AsTask();
        await Assert.ThrowsAsync<UpstreamException>(()=>locks.AcquireAsync("a",default).AsTask());
        await Assert.ThrowsAsync<UpstreamException>(()=>locks.AcquireAsync("b",default).AsTask());
        ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiting);
        owner.Dispose();using(await locks.AcquireAsync("b",default)) { }
        Assert.Equal(0,locks.Count);
    }
    [Fact]
    public async Task WeightedTenantLruTtlAndProbeChurnAreBoundedAndCleanupReferences()
    {
        await using var pool=new ResponsesWebSocketPool();var time=new Clock();
        using var transport=new ResponsesUpstreamTransport(null!,pool,NullLogger<ResponsesUpstreamTransport>.Instance,
            new() { MaxBytes=10000,MaxTenantBytes=5000,MaxSnapshotBytes=5000,MaxSnapshots=10,SnapshotTtlSeconds=30,MaxProbes=4,ProbeTtlSeconds=10 },time);
        Store(transport,"a","scope-a","r1");var initial=transport.CacheUsage.Bytes;Assert.True(initial>1000);
        Store(transport,"b","scope-b","r2");
        time.Advance(1);Store(transport,"a","scope-a","r3");time.Advance(1);Store(transport,"a","scope-a","r4");
        Assert.True(transport.CacheUsage.Bytes<=10000);Assert.Equal(2,transport.CacheUsage.Tenants);
        Assert.Null(Invoke(transport,"PlanContextRequest","scope-a",new D { ["previous_response_id"]="r1",["input"]=new List<object?>() }));
        Assert.NotNull(Invoke(transport,"PlanContextRequest","scope-b",new D { ["previous_response_id"]="r2",["input"]=new List<object?>() }));
        for(var i=0;i<100;i++)Invoke(transport,"MarkProbe",i.ToString(),true);
        Assert.Equal(4,transport.CacheUsage.Probes);
        time.Advance(11);transport.CleanupExpired();Assert.Equal(0,transport.CacheUsage.Probes);
        time.Advance(31);transport.CleanupExpired();Assert.Equal((0L,0,0,0,0),transport.CacheUsage);
        Assert.Null(Invoke(transport,"PlanContextRequest","scope-b",new D { ["previous_response_id"]="r2",["input"]=new List<object?>() }));
    }
    [Fact]
    public async Task OversizeSnapshotIsNotClonedOrCachedAndReadHitsRefreshIdleTtl()
    {
        await using var pool=new ResponsesWebSocketPool();var time=new Clock();
        using var transport=new ResponsesUpstreamTransport(null!,pool,NullLogger<ResponsesUpstreamTransport>.Instance,
            new() { MaxSnapshotBytes=5000,SnapshotTtlSeconds=30 },time);
        Store(transport,"a","s","too-big",10000);Assert.Equal(0,transport.CacheUsage.Snapshots);
        Store(transport,"a","s","r");time.Advance(20);
        Assert.NotNull(Invoke(transport,"PlanContextRequest","s",new D { ["previous_response_id"]="r",["input"]=new List<object?>() }));
        time.Advance(20);transport.CleanupExpired();Assert.Equal(1,transport.CacheUsage.Snapshots);
        // Replacement must remove only the old version, keeping latest and byte references exact.
        var bytes=transport.CacheUsage.Bytes;Store(transport,"a","s","r");Assert.Equal(bytes,transport.CacheUsage.Bytes);
        time.Advance(31);transport.CleanupExpired();Assert.Equal((0L,0,0,0,0),transport.CacheUsage);
    }
    private static void Store(ResponsesUpstreamTransport transport,string tenant,string scope,string id,int chars=1000)=>
        Invoke(transport,"Store",tenant,scope,new D { ["input"]=new List<object?> { new string('x',chars) } },new D { ["id"]=id,["output"]=new List<object?>() });
    private static object? Invoke(object instance,string method,params object[] arguments)=>instance.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(instance,arguments);
    private sealed class Clock:TimeProvider
    {
        private DateTimeOffset _now=new(2026,11,10,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>_now;
        public void Advance(int seconds)=>_now=_now.AddSeconds(seconds);
    }
}

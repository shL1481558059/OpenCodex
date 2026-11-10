using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenCodex.Core.Errors;
namespace OpenCodex.Core.Services.MultiAgent;
public sealed class MultiAgentRunCacheOptions
{
    public long MaxBytes {get;set;}=128L*1024*1024;
    public long MaxTenantBytes {get;set;}=32L*1024*1024;
    public long MaxRunBytes {get;set;}=16L*1024*1024;
    public long MaxColdLoadBytes {get;set;}=64L*1024*1024;
    public int MaxSessions {get;set;}=4096;
    public int MaxTenantSessions {get;set;}=512;
    public int MaxRunsPerSession {get;set;}=256;
    public int TtlSeconds {get;set;}=900;
    public int CleanupIntervalSeconds {get;set;}=60;
    public int MaxWaitersPerRun {get;set;}=128;
    public int MaxSafetyIdentities {get;set;}=65536;
    public int MaxTenantSafetyIdentities {get;set;}=8192;
    public int MaxSessionKeyCharacters {get;set;}=1024;
}
public sealed partial class MultiAgentRunStore:IDisposable
{
    private sealed class Resource(MultiAgentRun run,long weight){internal readonly MultiAgentRun Run=run;internal long Weight=weight;internal int References;internal bool Pinned;internal bool Durable;internal bool Rejected;}
    private sealed class SessionResource(Guid owner,long now){internal readonly Guid Owner=owner;internal long Accessed=now;}
    private readonly Dictionary<MultiAgentRun,Resource> _resources=[];
    private readonly Dictionary<string,SessionResource> _sessionResources=new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid,string),int> _safety=[];
    private readonly ConditionalWeakTable<MultiAgentRun,SemaphoreSlim> _gates=new();
    private readonly MultiAgentRunCacheOptions _limits;
    private readonly TimeProvider _clock;
    private readonly ITimer _timer;
    private bool _disposed;
    private long _bytes;
    private long _nextSweep;
    public MultiAgentRunStore(string directory):this(directory,null,null){}
    public MultiAgentRunStore(string directory,MultiAgentRunCacheOptions? options,TimeProvider? timeProvider=null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);_directory=Path.GetFullPath(directory);
        // Options are immutable for this registry, independent of later configuration mutation.
        _limits=System.Text.Json.JsonSerializer.Deserialize<MultiAgentRunCacheOptions>(System.Text.Json.JsonSerializer.Serialize(options??new()))!;
        foreach(var property in typeof(MultiAgentRunCacheOptions).GetProperties())
            if(Convert.ToInt64(property.GetValue(_limits))<=0 || Convert.ToInt64(property.GetValue(_limits))>long.MaxValue/8)throw new ArgumentOutOfRangeException(nameof(options));
        _clock=timeProvider??TimeProvider.System;var state=new SweepState(this);var period=TimeSpan.FromSeconds(_limits.CleanupIntervalSeconds);
        _timer=state.Timer=_clock.CreateTimer(static value=>((SweepState)value!).Tick(),state,period,period);
    }
    private sealed class SweepState(MultiAgentRunStore owner)
    {
        private readonly WeakReference<MultiAgentRunStore> _owner=new(owner);internal ITimer? Timer;
        internal void Tick(){if(!_owner.TryGetTarget(out var owner)){Timer?.Dispose();return;}if(!owner._indexGate.Wait(0))return;try{owner.Sweep();}finally{owner._indexGate.Release();}}
    }
    private long Now=>_clock.GetUtcNow().UtcTicks;
    private static ProxyException Limit()=>new("Multi-agent runtime cache admission limit reached; no history was truncated.",503);
    private static ProxyException Missing()=>new("The multi-agent runtime expired or was evicted. Start a new session with full history; the old runtime was not replayed.",409);
    private Resource Owned(MultiAgentRun run)=>_resources.TryGetValue(run,out var resource)?resource:throw Missing();
    private bool CanRetire(string key)=>_sessions[key].Values.All(run=>_resources.TryGetValue(run,out var resource)&&resource.References==0&&!resource.Pinned);
    private void Retire(string key)
    {
        var state=_sessionResources[key];var durable=true;
        foreach(var run in _sessions[key].Values){var resource=_resources[run];durable&=resource.Durable;_bytes-=resource.Weight;_resources.Remove(run);_gates.Remove(run);}
        _safety[(state.Owner,Hash(key))]=durable?1:2;_sessions.Remove(key);_latest.Remove(key);_sessionResources.Remove(key);
    }
    private void Sweep(bool force=false)
    {
        var now=Now;if(!force&&now<_nextSweep)return;_nextSweep=now+TimeSpan.FromSeconds(Math.Min(_limits.TtlSeconds,_limits.CleanupIntervalSeconds)).Ticks;
        foreach(var pair in _sessionResources.ToArray())
            if(now-pair.Value.Accessed>=TimeSpan.FromSeconds(_limits.TtlSeconds).Ticks&&CanRetire(pair.Key))Retire(pair.Key);
    }
    private void MakeRoom(Guid owner,long extra,int sessions,string? protect)
    {
        while(_bytes+extra>_limits.MaxBytes || _sessionResources.Count+sessions>_limits.MaxSessions
            || _sessionResources.Count(pair=>pair.Value.Owner==owner)+sessions>_limits.MaxTenantSessions
            || _resources.Values.Where(entry=>_sessionResources[entry.Run.SessionKey].Owner==owner).Sum(entry=>entry.Weight)+extra>_limits.MaxTenantBytes)
        {
            var tenantFull=_sessionResources.Count(pair=>pair.Value.Owner==owner)+sessions>_limits.MaxTenantSessions
                ||_resources.Values.Where(entry=>_sessionResources[entry.Run.SessionKey].Owner==owner).Sum(entry=>entry.Weight)+extra>_limits.MaxTenantBytes;
            string? victim=null;var oldest=long.MaxValue;
            foreach(var pair in _sessionResources)
                if(pair.Key!=protect&&(!tenantFull||pair.Value.Owner==owner)&&pair.Value.Accessed<oldest&&CanRetire(pair.Key)){victim=pair.Key;oldest=pair.Value.Accessed;}
            if(victim is null)throw Limit();Retire(victim);
        }
    }
    private void Reserve(Guid owner,string key)
    {
        var identity=(owner,Hash(key));
        if(_safety.TryGetValue(identity,out var blocked)){if(blocked==2)throw Missing();return;}
        if(_safety.Count>=_limits.MaxSafetyIdentities||_safety.Keys.Count(id=>id.Item1==owner)>=_limits.MaxTenantSafetyIdentities)throw Limit();
        _safety.Add(identity,0);
    }
    private void Register(MultiAgentRun run,bool durable)
    {
        var weight=Weight(run);if(weight>_limits.MaxRunBytes)throw Limit();
        var state=_sessionResources[run.SessionKey];MakeRoom(state.Owner,weight,0,run.SessionKey);
        if(_sessions[run.SessionKey].Count>=_limits.MaxRunsPerSession)throw Limit();
        _resources.Add(run,new(run,weight){Durable=durable});_sessions[run.SessionKey].Add(run.Id,run);_bytes+=weight;state.Accessed=Now;
    }
    // The historical public raw-Gate API has no observable waiter lifetime. Pin its
    // runtime rather than guessing CurrentCount and creating a second gate. It remains
    // quota bounded; production callers use AcquireRequestAsync and can be reclaimed.
    public SemaphoreSlim Gate(MultiAgentRun run)
    {
        _indexGate.Wait();try{ObjectDisposedException.ThrowIf(_disposed,this);Owned(run).Pinned=true;return _gates.GetValue(run,static _=>new(1,1));}finally{_indexGate.Release();}
    }
    public async Task<IAsyncDisposable> AcquireRequestAsync(MultiAgentRun run,CancellationToken ct)
    {
        Resource entry;SemaphoreSlim gate;
        await _indexGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);Sweep();entry=Owned(run);
            if(entry.Rejected||entry.Weight>_limits.MaxRunBytes||entry.References>=_limits.MaxWaitersPerRun+1L)throw Limit();
            entry.References++;_sessionResources[run.SessionKey].Accessed=Now;gate=_gates.GetValue(run,static _=>new(1,1));
        }
        finally{_indexGate.Release();}
        try{await gate.WaitAsync(ct);}
        catch{Release(entry);throw;}
        _indexGate.Wait();try{entry.Durable=false;}finally{_indexGate.Release();}
        return new RequestLease(this,entry,gate);
    }
    internal async Task<IAsyncDisposable> RetainAsync(MultiAgentRun run,CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);try{ObjectDisposedException.ThrowIf(_disposed,this);var entry=Owned(run);entry.References++;return new Retention(this,entry);}finally{_indexGate.Release();}
    }
    internal bool NeedsRetirement(MultiAgentRun run)
    { _indexGate.Wait();try{return !_resources.TryGetValue(run,out var entry)||entry.Rejected;}finally{_indexGate.Release();} }
    internal bool CompatibilityPinned(MultiAgentRun run)
    { _indexGate.Wait();try{return _resources.TryGetValue(run,out var entry)&&entry.Pinned;}finally{_indexGate.Release();} }
    private sealed class Retention(MultiAgentRunStore owner,Resource entry):IAsyncDisposable
    {
        private int _disposed;public ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _disposed,1)==0)owner.Release(entry);return ValueTask.CompletedTask;}
    }
    private void Release(Resource entry)
    {
        _indexGate.Wait();try{if(_sessionResources.TryGetValue(entry.Run.SessionKey,out var state))state.Accessed=Now;entry.References--;if(entry.Rejected&&_sessions.ContainsKey(entry.Run.SessionKey)&&CanRetire(entry.Run.SessionKey))Retire(entry.Run.SessionKey);}finally{_indexGate.Release();}
    }
    private sealed class RequestLease(MultiAgentRunStore owner,Resource entry,SemaphoreSlim gate):IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _disposed,1)==0){gate.Release();owner.Release(entry);}return ValueTask.CompletedTask;}
    }
    internal async Task CleanupAsync(){await _indexGate.WaitAsync();try{Sweep(true);}finally{_indexGate.Release();}}
    internal (int Sessions,int Runs,long Bytes,int Safety) Usage=> (_sessions.Count,_resources.Count,_bytes,_safety.Count);
    public void Dispose(){_timer.Dispose();_indexGate.Wait();try{_disposed=true;_sessions.Clear();_latest.Clear();_sessionResources.Clear();_resources.Clear();_safety.Clear();_bytes=0;}finally{_indexGate.Release();}}
    private static readonly Dictionary<Type,PropertyInfo[]> Properties=new[]{typeof(MultiAgentRun),typeof(MultiAgentState),typeof(MultiAgentPendingTask),typeof(MultiAgentTaskTurn)}.ToDictionary(type=>type,type=>type.GetProperties());
    private long Weight(object? value,int depth=0)
    {
        if(depth>64)return _limits.MaxRunBytes+1;
        long total=64;
        switch(value)
        {
            case null:return 8;
            case string text:return 24L+2L*text.Length;
            case IDictionary dictionary:
                foreach(DictionaryEntry pair in dictionary){total+=64+Weight(pair.Key,depth+1)+Weight(pair.Value,depth+1);if(total>_limits.MaxRunBytes)return total;}break;
            case IEnumerable items:
                foreach(var item in items){total+=16+Weight(item,depth+1);if(total>_limits.MaxRunBytes)return total;}break;
            default:
                if(Properties.TryGetValue(value.GetType(),out var properties))foreach(var property in properties){total+=Weight(property.GetValue(value),depth+1);if(total>_limits.MaxRunBytes)return total;}
                else if(!value.GetType().IsValueType)return _limits.MaxRunBytes+1;
                break;
        }
        return total;
    }
}

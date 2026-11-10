using OpenCodex.Core.Errors;
namespace OpenCodex.Core.Services.MultiAgent;
public sealed class MultiAgentBindingCacheOptions
{
    public int MaxModelWaiters {get;set;}=128;
    public int MaxGroups {get;set;}=512;
    public int MaxTenantGroups {get;set;}=128;
    public int MaxBindings {get;set;}=4096;
    public int MaxTenantBindings {get;set;}=1024;
    public int MaxGroupBindings {get;set;}=256;
    public int MaxIdentityCharacters {get;set;}=512;
    public int MaxManifestBytes {get;set;}=4*1024*1024;
    public int TtlSeconds {get;set;}=900;
    public int CleanupIntervalSeconds {get;set;}=60;
    public int MaxSafetyIdentities {get;set;}=65536;
    public int MaxTenantSafetyIdentities {get;set;}=8192;
}
public sealed partial class MultiAgentClientStore:IDisposable
{
    private readonly MultiAgentBindingCacheOptions _limits;
    private readonly TimeProvider _clock;
    private readonly ITimer _timer;
    private readonly Dictionary<(Guid,string),int> _retired=[];
    private volatile bool _disposed;
    public MultiAgentClientStore(MultiAgentRunStore runs,string directory):this(runs,directory,null,null){}
    public MultiAgentClientStore(MultiAgentRunStore runs,string directory,MultiAgentBindingCacheOptions? options,TimeProvider? timeProvider=null)
    {
        ArgumentNullException.ThrowIfNull(runs);ArgumentException.ThrowIfNullOrWhiteSpace(directory);_runs=runs;_directory=Path.Combine(Path.GetFullPath(directory),"client-bindings");
        _limits=System.Text.Json.JsonSerializer.Deserialize<MultiAgentBindingCacheOptions>(System.Text.Json.JsonSerializer.Serialize(options??new()))!;
        foreach(var property in typeof(MultiAgentBindingCacheOptions).GetProperties())if((int)property.GetValue(_limits)!<=0)throw new ArgumentOutOfRangeException(nameof(options));
        _clock=timeProvider??TimeProvider.System;var state=new SweepState(this);var period=TimeSpan.FromSeconds(_limits.CleanupIntervalSeconds);
        _timer=state.Timer=_clock.CreateTimer(static value=>((SweepState)value!).Tick(),state,period,period);
    }
    private sealed class SweepState(MultiAgentClientStore store)
    {
        private readonly WeakReference<MultiAgentClientStore> _owner=new(store);internal ITimer? Timer;
        internal void Tick(){if(!_owner.TryGetTarget(out var owner)){Timer?.Dispose();return;}if(!owner._indexGate.Wait(0))return;try{owner.Sweep();}finally{owner._indexGate.Release();}}
    }
    private long Now=>_clock.GetUtcNow().UtcTicks;
    private static ProxyException Limit()=>new("Multi-agent binding cache admission limit reached.",503);
    private static ProxyException Missing()=>new("The client binding expired or was evicted; start a new thread with full history. It was not replayed.",409);
    private void Reserve(Guid owner,string root)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(root.Length>_limits.MaxIdentityCharacters)throw Limit();
        var key=(owner,HashIdentity(root));if(_retired.TryGetValue(key,out var blocked)){if(blocked==2)throw Missing();return;}
        if(_retired.Count>=_limits.MaxSafetyIdentities||_retired.Keys.Count(id=>id.Item1==owner)>=_limits.MaxTenantSafetyIdentities)throw Limit();
        _retired.Add(key,0);
    }
    private static string HashIdentity(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private bool Active(BindingGroup group)
    {
        if(Volatile.Read(ref group.ModelReferences)!=0)return true;
        lock(_activityGate)if(group.Bindings.Values.Any(binding=>_activities.ContainsKey(binding)))return true;
        return !_disposed&&group.Bindings.Values.Any(binding=>_runs.CompatibilityPinned(binding.Run));
    }
    private void Remove(string key,BindingGroup group)
    {
        _groups.Remove(key);_retired[(group.Root.Owner,HashIdentity(group.Root.RootThreadId))]=group.Root.Persist?1:2;
        foreach(var hold in group.Holds)hold.DisposeAsync().GetAwaiter().GetResult();group.Holds.Clear();
        foreach(var binding in group.Bindings.Values)binding.Stop.Dispose();group.ChildModels.Dispose();
    }
    private void Sweep()
    {
        foreach(var pair in _groups.ToArray())
            if((_disposed||pair.Value.Bindings.Values.Any(binding=>_runs.NeedsRetirement(binding.Run))||Now-pair.Value.Accessed>=TimeSpan.FromSeconds(_limits.TtlSeconds).Ticks)&&!Active(pair.Value))Remove(pair.Key,pair.Value);
    }
    private void MakeRoom(Guid owner,int groups,int bindings,string? protect=null)
    {
        while(_groups.Count+groups>_limits.MaxGroups || _groups.Values.Count(group=>group.Root.Owner==owner)+groups>_limits.MaxTenantGroups
            ||_groups.Values.Sum(group=>group.Bindings.Count)+bindings>_limits.MaxBindings
            ||_groups.Values.Where(group=>group.Root.Owner==owner).Sum(group=>group.Bindings.Count)+bindings>_limits.MaxTenantBindings)
        {
            var tenantFull=_groups.Values.Count(group=>group.Root.Owner==owner)+groups>_limits.MaxTenantGroups
                ||_groups.Values.Where(group=>group.Root.Owner==owner).Sum(group=>group.Bindings.Count)+bindings>_limits.MaxTenantBindings;
            var victim=_groups.Where(pair=>pair.Key!=protect&&(!tenantFull||pair.Value.Root.Owner==owner)&&!Active(pair.Value)).OrderBy(pair=>pair.Value.Accessed).FirstOrDefault();
            if(victim.Value is null)throw Limit();Remove(victim.Key,victim.Value);
        }
    }
    private async Task HoldGroup(BindingGroup group,CancellationToken ct)
    {
        MakeRoom(group.Root.Owner,1,group.Bindings.Count);group.Accessed=Now;
        try{foreach(var binding in group.Bindings.Values)group.Holds.Add(await _runs.RetainAsync(binding.Run,ct));}
        catch{foreach(var hold in group.Holds)await hold.DisposeAsync();group.Holds.Clear();throw;}
    }
    private void ReleaseModel(BindingGroup group)
    {
        _indexGate.Wait();try{group.Accessed=Now;group.ModelReferences--;if(_disposed||group.Bindings.Values.Any(binding=>_runs.NeedsRetirement(binding.Run)))Sweep();}finally{_indexGate.Release();}
    }
    internal async Task CleanupAsync(){await _indexGate.WaitAsync();try{Sweep();}finally{_indexGate.Release();}}
    internal int CachedGroups=>_groups.Count;
    public void Dispose(){_timer.Dispose();_indexGate.Wait();try{_disposed=true;Sweep();}finally{_indexGate.Release();}}
}

namespace OpenCodex.Core.Services.MultiAgent;

public sealed class ClientConnectionCacheOptions
{
    public long MaxBytes { get; set; } = 128L * 1024 * 1024;
    public long MaxTenantBytes { get; set; } = 32L * 1024 * 1024;
    public long MaxConnectionBytes { get; set; } = 16L * 1024 * 1024;
    public long MaxSnapshotBytes { get; set; } = 8L * 1024 * 1024;
    public int MaxEntries { get; set; } = 4096;
    public int MaxConnectionEntries { get; set; } = 256;
    public int TtlSeconds { get; set; } = 900;
    public int CleanupIntervalSeconds { get; set; } = 60;
    public int MaxKeyCharacters { get; set; } = 512;
}

/// <summary>Process/tenant/connection budgets for optional WS history and interrupt IDs.
/// Eviction never invokes the model: continuation misses require full client history.</summary>
public sealed class ClientConnectionCache : IDisposable
{
    private sealed record Entry(Connection Scope, string Key, object Value, long Bytes) { internal long LastAccess; }
    private readonly object _gate = new();
    private readonly Dictionary<(Connection Scope, string Key), Entry> _entries = [];
    private readonly ClientConnectionCacheOptions _options;
    private readonly TimeProvider _time;
    private readonly ITimer _cleanup;
    private long _bytes;
    private bool _disposed;
    public ClientConnectionCache(ClientConnectionCacheOptions? options = null, TimeProvider? timeProvider = null)
    {
        var input = options ?? new();
        _options = new() { MaxBytes=input.MaxBytes,MaxTenantBytes=input.MaxTenantBytes,MaxConnectionBytes=input.MaxConnectionBytes,
            MaxSnapshotBytes=input.MaxSnapshotBytes,MaxEntries=input.MaxEntries,MaxConnectionEntries=input.MaxConnectionEntries,
            TtlSeconds=input.TtlSeconds,CleanupIntervalSeconds=input.CleanupIntervalSeconds,MaxKeyCharacters=input.MaxKeyCharacters };
        if (_options.MaxBytes<=0 || _options.MaxBytes>long.MaxValue/8 || _options.MaxTenantBytes<=0 || _options.MaxTenantBytes>long.MaxValue/8
            || _options.MaxConnectionBytes<=0 || _options.MaxConnectionBytes>long.MaxValue/8 || _options.MaxSnapshotBytes<=0 || _options.MaxSnapshotBytes>long.MaxValue/8
            || _options.MaxEntries<=0 || _options.MaxConnectionEntries<=0 || _options.TtlSeconds<=0 || _options.CleanupIntervalSeconds<=0 || _options.MaxKeyCharacters<=0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _time = timeProvider ?? TimeProvider.System;
        var sweep = new SweepState(this); var period = TimeSpan.FromSeconds(_options.CleanupIntervalSeconds);
        _cleanup = sweep.Timer = _time.CreateTimer(static state => ((SweepState)state!).Tick(), sweep, period, period);
    }
    private sealed class SweepState(ClientConnectionCache cache)
    {
        private readonly WeakReference<ClientConnectionCache> _owner = new(cache);
        internal ITimer? Timer;
        internal void Tick() { if (_owner.TryGetTarget(out var owner)) owner.Cleanup(); else Timer?.Dispose(); }
    }
    public Connection Open(Guid tenant) { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed,this); return new(this,tenant); } }
    private long Cap => Math.Min(_options.MaxSnapshotBytes,Math.Min(_options.MaxConnectionBytes,Math.Min(_options.MaxTenantBytes,_options.MaxBytes)));
    private bool Put(Connection scope,string key,object value,long weight)
    {
        lock (_gate)
        {
            if (_disposed || scope.Closed || key.Length>_options.MaxKeyCharacters+2 || weight>Cap) return false;
            Sweep();
            Remove((scope,key));
            while (_entries.Values.Count(e=>ReferenceEquals(e.Scope,scope))>=_options.MaxConnectionEntries
                || _entries.Values.Where(e=>ReferenceEquals(e.Scope,scope)).Sum(e=>e.Bytes)+weight>_options.MaxConnectionBytes)
                RemoveOldest(e=>ReferenceEquals(e.Scope,scope));
            while (_entries.Values.Where(e=>e.Scope.Tenant==scope.Tenant).Sum(e=>e.Bytes)+weight>_options.MaxTenantBytes)
                RemoveOldest(e=>e.Scope.Tenant==scope.Tenant);
            while (_entries.Count>=_options.MaxEntries || _bytes+weight>_options.MaxBytes) RemoveOldest(static _=>true);
            var entry = new Entry(scope,key,value,weight) { LastAccess=_time.GetUtcNow().UtcTicks };
            _entries.Add((scope,key),entry);_bytes+=weight;return true;
        }
    }
    private void RemoveOldest(Func<Entry,bool> predicate)
    {
        var victim = _entries.Values.Where(predicate).MinBy(e=>e.LastAccess) ?? throw new InvalidOperationException("Cache admission accounting is inconsistent.");
        Remove((victim.Scope,victim.Key));
    }
    private object? Get(Connection scope,string key)
    {
        lock (_gate)
        {
            if (_disposed || scope.Closed) return null;
            Sweep();
            if (!_entries.TryGetValue((scope,key),out var entry)) return null;
            entry.LastAccess=_time.GetUtcNow().UtcTicks;return entry.Value;
        }
    }
    private void Remove((Connection Scope,string Key) key) { if (_entries.Remove(key,out var entry)) _bytes-=entry.Bytes; }
    private void Sweep()
    {
        var deadline=_time.GetUtcNow().UtcTicks-TimeSpan.FromSeconds(_options.TtlSeconds).Ticks;
        foreach(var entry in _entries.Values.Where(e=>e.LastAccess<=deadline).ToArray())Remove((entry.Scope,entry.Key));
    }
    internal void Cleanup() { lock(_gate)Sweep(); }
    internal (int Entries,long Bytes) Usage { get { lock(_gate)return (_entries.Count,_bytes); } }
    public void Dispose() { _cleanup.Dispose();lock(_gate) { _disposed=true;_entries.Clear();_bytes=0; } }

    public sealed class Connection : IDisposable
    {
        private readonly ClientConnectionCache _owner;
        internal readonly Guid Tenant;
        internal bool Closed;
        internal Connection(ClientConnectionCache owner,Guid tenant) { _owner=owner;Tenant=tenant; }
        public bool RememberResponse(string id,object ownedRecord,object request,object response)
        {
            if(id.Length>_owner._options.MaxKeyCharacters)return false;
            var weight=2*(ClientGraphWeight.Estimate(request,_owner.Cap)+ClientGraphWeight.Estimate(response,_owner.Cap))+512+4L*id.Length;
            return _owner.Put(this,"r:"+id,ownedRecord,weight);
        }
        public T? FindResponse<T>(string id) where T:class => id.Length>_owner._options.MaxKeyCharacters?null:_owner.Get(this,"r:"+id) as T;
        public void RememberId(string id)
        {
            if(id.Length<=_owner._options.MaxKeyCharacters)_owner.Put(this,"i:"+id,id,256+4L*id.Length);
        }
        public bool Knows(string id) => id.Length<=_owner._options.MaxKeyCharacters && _owner.Get(this,"i:"+id) is not null;
        public void Dispose()
        {
            lock(_owner._gate)
            {
                if(Closed)return;Closed=true;
                foreach(var key in _owner._entries.Keys.Where(k=>ReferenceEquals(k.Scope,this)).ToArray())_owner.Remove(key);
            }
        }
    }
}

using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>References include the owner AND registered waiters, before WaitAsync starts.
/// Removal and registration share one gate: an idle key can never have two live semaphores.</summary>
internal sealed class RefCountedKeyedLock(int maxKeys,int maxWaiters)
{
    private readonly object _gate=new();
    private readonly Dictionary<string,Entry> _entries=new(StringComparer.Ordinal);
    internal int Count { get { lock(_gate)return _entries.Count; } }
    internal async ValueTask<IDisposable> AcquireAsync(string key,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();Entry entry;
        lock(_gate)
        {
            if(!_entries.TryGetValue(key,out entry!))
            {
                if(_entries.Count>=maxKeys)throw new UpstreamException("The active conversation lock quota is exhausted.",503);
                _entries.Add(key,entry=new());
            }
            if(entry.References>=maxWaiters+1)throw new UpstreamException("The conversation waiter quota is exhausted.",503);
            entry.References++;
        }
        try { await entry.Semaphore.WaitAsync(ct);return new Lease(this,key,entry); }
        catch { Release(key,entry,false);throw; }
    }
    private void Release(string key,Entry entry,bool owned)
    {
        lock(_gate)
        {
            if(owned)entry.Semaphore.Release();
            if(--entry.References!=0)return;
            _entries.Remove(key);entry.Semaphore.Dispose();
        }
    }
    private sealed class Entry { internal readonly SemaphoreSlim Semaphore=new(1,1);internal int References; }
    private sealed class Lease(RefCountedKeyedLock owner,string key,Entry entry):IDisposable
    {
        private Entry? _entry=entry;
        public void Dispose() { var e=Interlocked.Exchange(ref _entry,null);if(e is not null)owner.Release(key,e,true); }
    }
}

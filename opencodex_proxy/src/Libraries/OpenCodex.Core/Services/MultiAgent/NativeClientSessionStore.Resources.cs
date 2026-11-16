using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Services.MultiAgent;

public sealed partial class NativeClientSessionStore : IDisposable
{
    private NativeClientCacheOptions _options = new();
    private TimeProvider _time = TimeProvider.System;
    private ITimer? _cleanup;
    private long _nextSweep;
    private readonly Dictionary<Guid, int> _tenantSessionCounts = [];
    private volatile bool _disposed;

    private void InitializeResources(NativeClientCacheOptions? options, TimeProvider? timeProvider)
    {
        _options = (options ?? new()).ValidatedCopy();
        _time = timeProvider ?? TimeProvider.System;
        var state = new SweepState(this);
        var period = TimeSpan.FromSeconds(_options.CleanupIntervalSeconds);
        _cleanup = state.Timer = _time.CreateTimer(static value => ((SweepState)value!).Tick(), state, period, period);
    }

    private sealed class SweepState(NativeClientSessionStore store)
    {
        private readonly WeakReference<NativeClientSessionStore> _store = new(store);
        internal ITimer? Timer;

        internal void Tick()
        {
            if (!_store.TryGetTarget(out var target)) { Timer?.Dispose(); return; }
            if (!target._indexGate.Wait(0)) return;
            try { target.Sweep(force: true); }
            finally { target._indexGate.Release(); }
        }
    }

    private long Now => _time.GetUtcNow().UtcTicks;

    private static ProxyException Quota(string resource)
        => new($"Native client {resource} admission limit reached.", 503);

    private void ValidateSize(MultiAgentClientIdentity identity)
    {
        if (identity.ThreadId.Length > _options.MaxIdentityCharacters || identity.RootThreadId.Length > _options.MaxIdentityCharacters
            || identity.ParentThreadId.Length > _options.MaxIdentityCharacters || identity.AgentName.Length > _options.MaxIdentityCharacters)
            throw new BadRequestException("Native client identity exceeds MaxIdentityCharacters.");
    }

    private void CheckIdleSession(NativeClientSession? session)
    {
        if (session is not null && Now - Interlocked.Read(ref session.LastAccess) >= TimeSpan.FromSeconds(_options.SessionTtlSeconds).Ticks)
            Sweep(force: true);
    }

    private void CheckIdleGroup(Guid owner, string root)
    {
        if (_groups.TryGetValue(SessionKey(owner, root), out var group)
            && Now - Interlocked.Read(ref group.LastAccess) >= TimeSpan.FromSeconds(_options.SessionTtlSeconds).Ticks)
            Sweep(force: true);
    }

    private void Touch(NativeClientSession session)
    {
        var now = Now;
        Interlocked.Exchange(ref session.LastAccess, now);
        Interlocked.Exchange(ref session.Group.LastAccess, now);
    }

    private void ReleaseReference(NativeClientSession session)
    {
        Touch(session);
        Interlocked.Decrement(ref session.RequestReferences);
        if (!_disposed) return;
        _indexGate.Wait();
        try { Sweep(); }
        finally { _indexGate.Release(); }
    }

    private void MakeRoom(Guid owner, int count, string? protectedRoot = null)
    {
        if (count > _options.MaxGroupSessions || count > _options.MaxSessions || count > _options.MaxTenantSessions)
            throw Quota("session group");
        while (_sessions.Count + count > _options.MaxSessions
            || _tenantSessionCounts.GetValueOrDefault(owner) + count > _options.MaxTenantSessions)
        {
            var tenantFull = _tenantSessionCounts.GetValueOrDefault(owner) + count > _options.MaxTenantSessions;
            NativeClientGroup? victim = null;
            string? victimKey = null;
            long oldest = long.MaxValue;
            foreach (var pair in _groups)
            {
                var group = pair.Value;
                if ((group.Owner == owner && group.RootThreadId == protectedRoot)
                    || (tenantFull && group.Owner != owner) || HasReferences(group)) continue;
                var accessed = Interlocked.Read(ref group.LastAccess);
                if (accessed >= oldest) continue;
                oldest = accessed;
                victim = group;
                victimKey = pair.Key;
            }
            if (victim is null) throw Quota("active session");
            RemoveGroup(victimKey!, victim);
        }
    }

    private bool HasReferences(NativeClientGroup group)
    {
        foreach (var thread in group.Agents.Values)
            if (FindSession(group.Owner, thread) is { } session && Volatile.Read(ref session.RequestReferences) != 0)
                return true;
        return false;
    }

    private List<NativeClientSession> Members(NativeClientGroup group) => group.Agents.Values
        .Select(thread => FindSession(group.Owner, thread)).OfType<NativeClientSession>().ToList();

    private void Sweep(bool force = false)
    {
        var now = Now;
        if (!force && !_disposed && now < _nextSweep) return;
        _nextSweep = now + TimeSpan.FromSeconds(Math.Min(_options.CleanupIntervalSeconds, _options.SessionTtlSeconds)).Ticks;
        foreach (var pair in _groups.ToArray())
        {
            if (!_disposed && now - Interlocked.Read(ref pair.Value.LastAccess) < TimeSpan.FromSeconds(_options.SessionTtlSeconds).Ticks)
                continue;
            if (!HasReferences(pair.Value)) RemoveGroup(pair.Key, pair.Value);
        }
    }

    private void RemoveGroup(string key, NativeClientGroup group)
    {
        var members = Members(group);
        if (members.Any(session => Volatile.Read(ref session.RequestReferences) != 0))
            throw new InvalidOperationException("Cannot reclaim an active native group.");
        foreach (var session in members)
        {
            if (!_sessions.TryRemove(SessionKey(session.Owner, session.ThreadId), out _)) continue;
            var count = _tenantSessionCounts[session.Owner] - 1;
            if (count == 0) _tenantSessionCounts.Remove(session.Owner);
            else _tenantSessionCounts[session.Owner] = count;
            session.Gate.Dispose();
        }
        _groups.Remove(key);
        group.ChildModels.Dispose();
    }

    internal async Task CleanupAsync(CancellationToken ct = default)
    {
        await _indexGate.WaitAsync(ct);
        try { Sweep(force: true); }
        finally { _indexGate.Release(); }
    }

    internal async Task<(int Sessions, int Groups, int Responses, int Replays, int Pools, long Bytes,
        int SafetyIdentities, int References, int ReplayIdentities)> ResourceUsageAsync()
    {
        await _indexGate.WaitAsync();
        try
        {
            return (_sessions.Count, _groups.Count, 0, 0, 0, 0, 0,
                _sessions.Values.Sum(session => Volatile.Read(ref session.RequestReferences)), 0);
        }
        finally { _indexGate.Release(); }
    }

    public void Dispose()
    {
        _cleanup?.Dispose();
        _indexGate.Wait();
        try
        {
            _disposed = true;
            Sweep();
        }
        finally { _indexGate.Release(); }
    }
}

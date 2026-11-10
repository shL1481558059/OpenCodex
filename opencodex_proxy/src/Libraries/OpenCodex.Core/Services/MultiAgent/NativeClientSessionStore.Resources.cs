using System.Security.Cryptography;
using System.Text;
using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Services.MultiAgent;

public sealed partial class NativeClientSessionStore : IDisposable
{
    private readonly NativeClientCacheOptions _options;
    private readonly TimeProvider _time;
    private readonly ITimer _cleanup;
    private readonly Dictionary<(Guid Owner, string Hash), bool> _ephemeralIdentities = [];
    private long _memoryBytes;
    private long _nextSweep;
    private readonly Dictionary<Guid,int> _ephemeralTenantCounts = [];
    private readonly Dictionary<Guid,int> _tenantSessionCounts = [];
    private int _replayIdentityCount;
    private readonly Dictionary<Guid,int> _tenantReplayCounts = [];
    private volatile bool _disposed;

    public NativeClientSessionStore(string directory, NativeClientCacheOptions? options = null, TimeProvider? timeProvider = null)
    {
        _directory = Path.Combine(Path.GetFullPath(directory), "native-client-v1");
        _options = (options ?? new()).ValidatedCopy();
        _time = timeProvider ?? TimeProvider.System;
        var sweep = new SweepState(this);
        var period = TimeSpan.FromSeconds(_options.CleanupIntervalSeconds);
        _cleanup = sweep.Timer = _time.CreateTimer(static state => ((SweepState)state!).Tick(), sweep, period, period);
    }
    private sealed class SweepState(NativeClientSessionStore store)
    {
        private readonly WeakReference<NativeClientSessionStore> _store = new(store);
        internal ITimer? Timer;
        internal void Tick()
        {
            if (!_store.TryGetTarget(out var target)) { Timer?.Dispose(); return; }
            if (!target._indexGate.Wait(0)) return;
            try { target.Sweep(force:true); } finally { target._indexGate.Release(); }
        }
    }
    private long Now => _time.GetUtcNow().UtcTicks;
    private static ProxyException Unavailable() => new("The native client continuation or replay was evicted. Start a new client thread and turn with full history; the old turn was not re-executed.", 409);
    private static ProxyException Quota(string resource) => new($"Native client {resource} admission limit reached.", 503);
    private static string IdentityHash(string thread) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(thread)));
    private void CheckRetired(Guid owner, string thread)
    {
        if (_ephemeralIdentities.GetValueOrDefault((owner, IdentityHash(thread)))) throw Unavailable();
    }
    private void ReserveIdentities(Guid owner, string thread, string root)
    {
        var slots = new[] { (Owner: owner, Hash: IdentityHash(root)), (Owner: owner, Hash: IdentityHash(thread)) }
            .Distinct().Where(key => !_ephemeralIdentities.ContainsKey(key)).ToArray();
        if (_ephemeralIdentities.Count + slots.Length > _options.MaxEphemeralIdentities
            || _ephemeralTenantCounts.GetValueOrDefault(owner) + slots.Length > _options.MaxTenantEphemeralIdentities)
            throw Quota("ephemeral replay-safety identity");
        foreach (var key in slots) _ephemeralIdentities.Add(key, false);
        if (slots.Length > 0) _ephemeralTenantCounts[owner] = _ephemeralTenantCounts.GetValueOrDefault(owner) + slots.Length;
    }
    private void ValidateSize(MultiAgentClientIdentity identity)
    {
        if (identity.ThreadId.Length > _options.MaxIdentityCharacters || identity.RootThreadId.Length > _options.MaxIdentityCharacters
            || identity.ParentThreadId.Length > _options.MaxIdentityCharacters || identity.AgentName.Length > _options.MaxIdentityCharacters)
            throw new BadRequestException("Native client identity exceeds MaxIdentityCharacters.");
    }
    private void CheckIdleSession(NativeClientSession? session)
    {
        if (session is not null && Now - Interlocked.Read(ref session.LastAccess) >= TimeSpan.FromSeconds(_options.SessionTtlSeconds).Ticks) Sweep(force:true);
    }
    private bool Expired(StoredResponse record) => Now - record.LastAccess >= TimeSpan.FromSeconds(_options.SnapshotTtlSeconds).Ticks;
    private void CheckIdleGroup(Guid owner,string root)
    {
        if (_groups.TryGetValue(SessionKey(owner,root),out var group) && Now-Interlocked.Read(ref group.LastAccess)>=TimeSpan.FromSeconds(_options.SessionTtlSeconds).Ticks) Sweep(force:true);
    }
    private void Touch(NativeClientSession session)
    {
        var now=Now; Interlocked.Exchange(ref session.LastAccess,now); Interlocked.Exchange(ref session.Group.LastAccess,now);
    }
    private void ReleaseReference(NativeClientSession session)
    {
        Touch(session); // Publish fresh idle time BEFORE making this session reclaimable.
        Interlocked.Decrement(ref session.RequestReferences);
        if (_disposed)
        {
            _indexGate.Wait();
            try { Sweep(); } finally { _indexGate.Release(); }
        }
    }
    private void MakeRoom(Guid owner, int count, string? protectedRoot = null)
    {
        if (count > _options.MaxGroupSessions || count > _options.MaxSessions || count > _options.MaxTenantSessions) throw Quota("session group");
        while (_sessions.Count + count > _options.MaxSessions || _tenantSessionCounts.GetValueOrDefault(owner) + count > _options.MaxTenantSessions)
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
                oldest = accessed; victim = group; victimKey = pair.Key;
            }
            if (victim is null) throw Quota("active session");
            RemoveGroup(victimKey!, victim);
        }
    }
    private bool HasReferences(NativeClientGroup group)
    {
        foreach (var thread in group.Agents.Values)
            if (FindSession(group.Owner, thread) is { } session && Volatile.Read(ref session.RequestReferences) != 0) return true;
        return false;
    }
    private List<NativeClientSession> Members(NativeClientGroup group) => group.Agents.Values
        .Select(thread => FindSession(group.Owner, thread)).OfType<NativeClientSession>().ToList();
    private void Sweep(bool force = false)
    {
        var now = Now;
        if (!force && !_disposed && now < _nextSweep) return;
        _nextSweep = now + TimeSpan.FromSeconds(Math.Min(_options.CleanupIntervalSeconds,Math.Min(_options.SnapshotTtlSeconds,_options.SessionTtlSeconds))).Ticks;
        foreach (var record in _memoryResponses.Values.Where(r => now - r.LastAccess >= TimeSpan.FromSeconds(_options.SnapshotTtlSeconds).Ticks).ToArray()) RemoveRecord(record);
        foreach (var pair in _groups.ToArray())
        {
            if (!_disposed && now-Interlocked.Read(ref pair.Value.LastAccess)<TimeSpan.FromSeconds(_options.SessionTtlSeconds).Ticks) continue;
            if (!HasReferences(pair.Value)) RemoveGroup(pair.Key,pair.Value);
        }
    }
    private void RemoveGroup(string key, NativeClientGroup group)
    {
        var members = Members(group);
        if (members.Any(s => Volatile.Read(ref s.RequestReferences) != 0)) throw new InvalidOperationException("Cannot reclaim an active native group.");
        foreach (var session in members)
        {
            foreach (var record in _memoryResponses.Values.Where(r => r.Owner == session.Owner && r.ThreadId == session.ThreadId).ToArray()) RemoveRecord(record);
            _replayIdentityCount -= session.ReplayIdentities.Count;
            var tenantReplays = _tenantReplayCounts.GetValueOrDefault(session.Owner) - session.ReplayIdentities.Count;
            if (tenantReplays == 0) _tenantReplayCounts.Remove(session.Owner); else _tenantReplayCounts[session.Owner] = tenantReplays;
            _snapshotPools.Remove(SessionKey(session.Owner, session.ThreadId));
            if (_sessions.TryRemove(SessionKey(session.Owner, session.ThreadId), out _))
            {
                var count = _tenantSessionCounts[session.Owner] - 1;
                if (count == 0) _tenantSessionCounts.Remove(session.Owner); else _tenantSessionCounts[session.Owner] = count;
            }
            if (!session.Persist) _ephemeralIdentities[(session.Owner, IdentityHash(session.ThreadId))] = true;
            session.Gate.Dispose();
        }
        if (!group.Persist) _ephemeralIdentities[(group.Owner, IdentityHash(group.RootThreadId))] = true;
        _groups.Remove(key);
        group.ChildModels.Dispose();
    }
    private void RemoveRecord(StoredResponse record)
    {
        var responseKey = (record.Owner, record.ThreadId, record.ResponseId);
        if (!_memoryResponses.TryGetValue(responseKey, out var current) || !ReferenceEquals(current, record)) return;
        _memoryResponses.Remove(responseKey);
        foreach (var alias in _memoryReplays.Where(pair => ReferenceEquals(pair.Value, record)).Select(pair => pair.Key).ToArray()) _memoryReplays.Remove(alias);
        _memoryBytes -= record.Weight;
        // Intern tables must not keep graphs alive after response/replay aliases disappear.
        _snapshotPools.Remove(SessionKey(record.Owner, record.ThreadId));
    }
    private void CheckReplayCapacity(NativeClientSession session, string key)
    {
        if (key.Length == 0 || session.ReplayIdentities.Contains(key)) return;
        if (session.ReplayIdentities.Count >= _options.MaxSessionReplayIdentities || _replayIdentityCount >= _options.MaxReplayIdentities
            || _tenantReplayCounts.GetValueOrDefault(session.Owner) >= _options.MaxTenantReplayIdentities) throw Quota("replay-safety key");
    }
    private void RememberReplay(NativeClientSession session, string key)
    {
        if (key.Length == 0 || !session.ReplayIdentities.Add(key)) return;
        _replayIdentityCount++; _tenantReplayCounts[session.Owner] = _tenantReplayCounts.GetValueOrDefault(session.Owner) + 1;
    }
    private void AdmitRecord(StoredResponse record)
    {
        while (_memoryResponses.Values.Where(r => r.Owner == record.Owner).Sum(r => r.Weight) + record.Weight > _options.MaxTenantBytes
            || _memoryResponses.Values.Count(r => r.Owner == record.Owner) + 1 > _options.MaxTenantResponses)
            RemoveRecord(_memoryResponses.Values.Where(r => r.Owner == record.Owner).MinBy(r => r.LastAccess)!);
        while (_memoryBytes + record.Weight > _options.MaxBytes || _memoryResponses.Count + 1 > _options.MaxResponses)
            RemoveRecord(_memoryResponses.Values.MinBy(r => r.LastAccess)!);
        record.LastAccess = Now;

    }
    internal async Task CleanupAsync(CancellationToken ct = default)
    {
        await _indexGate.WaitAsync(ct);
        try { Sweep(force:true); } finally { _indexGate.Release(); }
    }
    internal async Task<(int Sessions, int Groups, int Responses, int Replays, int Pools, long Bytes, int SafetyIdentities, int References, int ReplayIdentities)> ResourceUsageAsync()
    {
        await _indexGate.WaitAsync();
        try { return (_sessions.Count, _groups.Count, _memoryResponses.Count, _memoryReplays.Count, _snapshotPools.Count, _memoryBytes, _ephemeralIdentities.Count, _sessions.Values.Sum(s => Volatile.Read(ref s.RequestReferences)), _replayIdentityCount); }
        finally { _indexGate.Release(); }
    }
    public void Dispose()
    {
        _cleanup.Dispose();
        _indexGate.Wait();
        try { _disposed = true; Sweep(); } finally { _indexGate.Release(); }
        // Active leases still release through this registry; never dispose their gates.
    }
}

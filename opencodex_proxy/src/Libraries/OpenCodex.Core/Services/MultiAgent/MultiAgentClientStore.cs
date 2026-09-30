using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>
/// Stores a versioned client binding manifest separately from each actor's runtime snapshot.
/// The root's initial persistence choice applies to its whole group. The metadata lock is never
/// held while waiting for an actor gate or a model permit. Callers hold Gate when changing a Run.
/// </summary>
public sealed class MultiAgentClientStore
{
    private readonly MultiAgentRunStore _runs;
    private readonly string _directory;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly Dictionary<string, BindingGroup> _groups = new(StringComparer.Ordinal);
    private readonly object _activityGate = new();
    private readonly Dictionary<MultiAgentClientBinding, MultiAgentClientActivity> _activities = new();

    public MultiAgentClientStore(MultiAgentRunStore runs, string directory)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _runs = runs;
        _directory = Path.Combine(Path.GetFullPath(directory), "client-bindings");
    }

    public SemaphoreSlim Gate(MultiAgentClientBinding binding) => _runs.Gate(binding.Run);

    public MultiAgentClientActivity BeginActivity(MultiAgentClientBinding binding, CancellationToken ct)
    {
        binding.Stop.Token.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        lock (_activityGate)
        {
            if (_activities.ContainsKey(binding))
                throw new InvalidOperationException("This client actor already has an active request.");
            var activity = new MultiAgentClientActivity(ct, binding.Stop.Token, completed =>
            {
                lock (_activityGate)
                    if (_activities.TryGetValue(binding, out var active) && ReferenceEquals(active, completed))
                        _activities.Remove(binding);
            });
            _activities.Add(binding, activity);
            return activity;
        }
    }

    public Task CancelActivityAsync(MultiAgentClientBinding binding)
    {
        MultiAgentClientActivity? activity;
        lock (_activityGate) _activities.TryGetValue(binding, out activity);
        return activity?.CancelAsync() ?? Task.CompletedTask;
    }

    public string? ActiveRequestId(MultiAgentClientBinding binding)
    {
        lock (_activityGate) return _activities.TryGetValue(binding, out var activity) ? activity.Id : null;
    }

    public Task CancelActivityAsync(MultiAgentClientBinding binding, string expectedActivityId)
    {
        MultiAgentClientActivity? activity;
        lock (_activityGate)
            activity = _activities.TryGetValue(binding, out var current) && current.Id == expectedActivityId ? current : null;
        return activity?.CancelAsync() ?? Task.CompletedTask;
    }

    public async Task<MultiAgentClientBinding> OpenRootAsync(Guid owner, string threadId,
        Func<MultiAgentRun> create, bool persist, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(create);
        await _indexGate.WaitAsync(ct);
        try
        {
            var existing = await LoadGroupAsync(owner, threadId, ct);
            if (existing is not null) return existing.Root;
            var id = Guid.NewGuid().ToString("N");
            var run = await _runs.GetAsync(SessionKey(owner, threadId, id), null, create, ct);
            ValidateActor(run);
            if (run.MaxConcurrentSubagents < 1)
                throw new ArgumentOutOfRangeException(nameof(create), "MaxConcurrentSubagents must be positive.");
            var root = new MultiAgentClientBinding
            {
                Id = id, Owner = owner, RootThreadId = threadId, ThreadId = threadId,
                AgentName = "/root", Run = run, Persist = persist, SpawnCompleted = true
            };
            var group = new BindingGroup(root, run.MaxConcurrentSubagents);
            await _runs.SaveAsync(run, ct, persist);
            await SaveManifestAsync(group, ct);
            _groups.Add(GroupKey(owner, threadId), group);
            return root;
        }
        finally { _indexGate.Release(); }
    }

    public async Task<MultiAgentClientBinding?> TryGetAsync(Guid owner, string rootThreadId,
        string threadId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        await _indexGate.WaitAsync(ct);
        try
        {
            var group = await LoadGroupAsync(owner, rootThreadId, ct);
            return group?.Bindings.Values.FirstOrDefault(binding => binding.ThreadId == threadId);
        }
        finally { _indexGate.Release(); }
    }

    public async Task<MultiAgentClientBinding?> TryGetAgentAsync(MultiAgentClientBinding caller,
        string target, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        await _indexGate.WaitAsync(ct);
        try
        {
            var group = OwnedGroup(caller);
            var name = target.StartsWith('/') ? target : caller.AgentName + "/" + target;
            return group.Bindings.Values.FirstOrDefault(binding => binding.AgentName == name && !binding.SpawnFailed);
        }
        finally { _indexGate.Release(); }
    }

    public async Task<MultiAgentClientBinding> ReserveAsync(MultiAgentClientBinding parent, string callId,
        string taskName, MultiAgentRun childRun, bool persist, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        if (taskName.Contains('/') || taskName is "." or "..")
            throw new ArgumentException("task_name must be one nonempty path segment.", nameof(taskName));
        ValidateActor(childRun);
        await _indexGate.WaitAsync(ct);
        try
        {
            var group = OwnedGroup(parent);
            if (parent.ThreadId.Length == 0 || parent.SpawnFailed)
                throw new InvalidOperationException("The parent must be bound to an active client thread.");
            var name = parent.AgentName + "/" + taskName;
            var duplicate = group.Bindings.Values.FirstOrDefault(binding =>
                binding.ParentName == parent.AgentName && binding.SpawnCallId == callId);
            if (duplicate is not null)
            {
                if (duplicate.AgentName != name || duplicate.Run.Model != childRun.Model || duplicate.SpawnFailed)
                    throw new InvalidOperationException("The spawn call has already been reserved with different parameters or failed.");
                return duplicate;
            }
            if (group.Bindings.Values.Any(binding => binding.AgentName == name && !binding.SpawnFailed))
                throw new InvalidOperationException("The agent already exists; use followup_task.");
            var id = Guid.NewGuid().ToString("N");
            var run = await _runs.GetAsync(SessionKey(parent.Owner, parent.RootThreadId, id), null, () => childRun, ct);
            var child = new MultiAgentClientBinding
            {
                Id = id, Owner = parent.Owner, RootThreadId = parent.RootThreadId,
                AgentName = name, ParentName = parent.AgentName, ParentThreadId = parent.ThreadId,
                SpawnCallId = callId, Run = run, Persist = group.Root.Persist
            };
            // Persist the seed before publishing the reservation to the client.
            await _runs.SaveAsync(run, ct, child.Persist);
            group.Bindings.Add(child.Id, child);
            try { await SaveManifestAsync(group, ct); }
            catch { group.Bindings.Remove(child.Id); throw; }
            return child;
        }
        finally { _indexGate.Release(); }
    }

    public async Task<MultiAgentClientBinding> AttachAsync(Guid owner, string rootThreadId,
        string parentThreadId, string threadId, string agentName, string model, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        await _indexGate.WaitAsync(ct);
        try
        {
            var group = await LoadGroupAsync(owner, rootThreadId, ct)
                ?? throw new KeyNotFoundException("The client multi-agent session is unavailable; memory-only sessions cannot be restored after restart.");
            var attached = group.Bindings.Values.FirstOrDefault(binding => binding.ThreadId == threadId);
            if (attached is not null)
            {
                if (attached.ParentThreadId != parentThreadId || attached.AgentName != agentName || attached.Run.Model != model || attached.SpawnFailed)
                    throw new InvalidOperationException("This client thread is already bound to a different or failed agent.");
                return attached;
            }
            var reserved = group.Bindings.Values.FirstOrDefault(binding => binding.ParentThreadId == parentThreadId
                && binding.AgentName == agentName && !binding.SpawnFailed)
                ?? throw new KeyNotFoundException("No matching server-side spawn reservation exists for this parent and agent.");
            if (reserved.ThreadId.Length != 0)
                throw new InvalidOperationException("This agent is already attached to another client thread.");
            if (reserved.Run.Model != model)
                throw new InvalidOperationException("The child request model does not match its server-side spawn reservation.");
            reserved.ThreadId = threadId;
            try { await SaveManifestAsync(group, ct); }
            catch { reserved.ThreadId = string.Empty; throw; }
            return reserved;
        }
        finally { _indexGate.Release(); }
    }

    public async Task CompleteSpawnAsync(MultiAgentClientBinding parent, string callId,
        bool succeeded, CancellationToken ct)
    {
        CancellationTokenSource? stop = null;
        await _indexGate.WaitAsync(ct);
        try
        {
            var group = OwnedGroup(parent);
            var child = group.Bindings.Values.FirstOrDefault(binding =>
                binding.ParentName == parent.AgentName && binding.SpawnCallId == callId);
            if (child is null) return;
            if (child.SpawnFailed)
            {
                if (succeeded) throw new InvalidOperationException("A failed spawn cannot subsequently complete successfully.");
                return;
            }
            if (child.SpawnCompleted)
            {
                if (!succeeded) throw new InvalidOperationException("A successful spawn cannot subsequently fail.");
                return;
            }
            child.SpawnCompleted = succeeded;
            child.SpawnFailed = !succeeded;
            try { await SaveManifestAsync(group, ct); }
            catch { child.SpawnCompleted = false; child.SpawnFailed = false; throw; }
            if (!succeeded) stop = child.Stop;
        }
        finally { _indexGate.Release(); }
        // Cancellation callbacks must not execute while holding the group index lock.
        if (stop is not null) await stop.CancelAsync();
    }

    public async Task SaveAsync(MultiAgentClientBinding binding, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { _ = OwnedGroup(binding); }
        finally { _indexGate.Release(); }
        // Only this actor's snapshot is serialized; siblings may currently be executing.
        await _runs.SaveAsync(binding.Run, ct, binding.Persist);
        await _indexGate.WaitAsync(ct);
        try { await SaveManifestAsync(OwnedGroup(binding), ct); }
        finally { _indexGate.Release(); }
    }

    public async Task<IAsyncDisposable> AcquireModelAsync(MultiAgentClientBinding binding, CancellationToken ct)
    {
        SemaphoreSlim? semaphore;
        await _indexGate.WaitAsync(ct);
        try
        {
            var group = OwnedGroup(binding);
            binding.Stop.Token.ThrowIfCancellationRequested();
            if (binding.SpawnFailed) throw new OperationCanceledException("The native spawn failed.");
            semaphore = binding.ParentName.Length == 0 ? null : group.ChildModels;
        }
        finally { _indexGate.Release(); }
        if (semaphore is null) return new ModelLease(null);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct, binding.Stop.Token);
        await semaphore.WaitAsync(cancelled.Token);
        return new ModelLease(semaphore);
    }

    private BindingGroup OwnedGroup(MultiAgentClientBinding binding)
    {
        if (!_groups.TryGetValue(GroupKey(binding.Owner, binding.RootThreadId), out var group)
            || !group.Bindings.TryGetValue(binding.Id, out var owned) || !ReferenceEquals(owned, binding))
            throw new InvalidOperationException("The client binding does not belong to this store.");
        return group;
    }

    private async Task<BindingGroup?> LoadGroupAsync(Guid owner, string rootThreadId, CancellationToken ct)
    {
        var key = GroupKey(owner, rootThreadId);
        if (_groups.TryGetValue(key, out var group)) return group;
        var path = ManifestPath(owner, rootThreadId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<BindingManifest>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("The client binding manifest is empty.");
        if (manifest.Version != 1 || manifest.Owner != owner || manifest.RootThreadId != rootThreadId
            || !manifest.Persist || manifest.MaxConcurrentSubagents < 1)
            throw new InvalidDataException("The client binding manifest is incompatible or belongs to another owner.");
        var bindings = new List<MultiAgentClientBinding>();
        foreach (var entry in manifest.Bindings)
        {
            if (entry.Id.Length == 0 || entry.RunSessionKey != SessionKey(owner, rootThreadId, entry.Id))
                throw new InvalidDataException("The client actor snapshot reference is invalid.");
            var run = await _runs.GetAsync(entry.RunSessionKey, null,
                () => throw new InvalidDataException("The client actor snapshot is unavailable."), ct);
            if (run.Id != entry.RunId) throw new InvalidDataException("The client actor snapshot identity does not match its manifest.");
            ValidateActor(run);
            var binding = new MultiAgentClientBinding
            {
                Id = entry.Id, Owner = owner, RootThreadId = rootThreadId, ThreadId = entry.ThreadId,
                AgentName = entry.AgentName, ParentName = entry.ParentName, ParentThreadId = entry.ParentThreadId,
                SpawnCallId = entry.SpawnCallId, SpawnCompleted = entry.SpawnCompleted,
                SpawnFailed = entry.SpawnFailed, Run = run, Persist = true
            };
            if (binding.SpawnFailed) binding.Stop.Cancel();
            bindings.Add(binding);
        }
        var roots = bindings.Where(binding => binding.AgentName == "/root" && binding.ParentName.Length == 0).ToList();
        if (roots.Count != 1 || roots[0].ThreadId != rootThreadId)
            throw new InvalidDataException("The client binding manifest must contain its root thread.");
        if (bindings.Select(binding => binding.Id).Distinct(StringComparer.Ordinal).Count() != bindings.Count
            || bindings.Where(binding => binding.ThreadId.Length != 0).GroupBy(binding => binding.ThreadId).Any(grouping => grouping.Count() != 1))
            throw new InvalidDataException("The client binding manifest contains duplicate actor identities.");
        group = new BindingGroup(roots[0], manifest.MaxConcurrentSubagents);
        foreach (var binding in bindings.Where(binding => !ReferenceEquals(binding, roots[0])))
            group.Bindings.Add(binding.Id, binding);
        _groups.Add(key, group);
        return group;
    }

    private async Task SaveManifestAsync(BindingGroup group, CancellationToken ct)
    {
        if (!group.Root.Persist) return;
        var manifest = new BindingManifest
        {
            Owner = group.Root.Owner, RootThreadId = group.Root.RootThreadId, Persist = true,
            MaxConcurrentSubagents = group.MaxConcurrentSubagents,
            Bindings = group.Bindings.Values.Select(binding => new BindingEntry
            {
                Id = binding.Id, ThreadId = binding.ThreadId, AgentName = binding.AgentName,
                ParentName = binding.ParentName, ParentThreadId = binding.ParentThreadId,
                SpawnCallId = binding.SpawnCallId, SpawnFailed = binding.SpawnFailed,
                SpawnCompleted = binding.SpawnCompleted, RunSessionKey = binding.Run.SessionKey, RunId = binding.Run.Id
            }).ToList()
        };
        Directory.CreateDirectory(_directory);
        var path = ManifestPath(group.Root.Owner, group.Root.RootThreadId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateActor(MultiAgentRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Agents.Count != 1 || !run.Agents.ContainsKey("/root"))
            throw new ArgumentException("Each client actor must contain exactly its own internal /root state.", nameof(run));
        ArgumentException.ThrowIfNullOrWhiteSpace(run.Model);
    }

    private string ManifestPath(Guid owner, string rootThreadId) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GroupKey(owner, rootThreadId)))) + ".json");
    private static string GroupKey(Guid owner, string rootThreadId) => owner.ToString("N") + "/" + rootThreadId;
    private static string SessionKey(Guid owner, string rootThreadId, string id) => "client-v1/" + GroupKey(owner, rootThreadId) + "/" + id;

    private sealed class BindingGroup(MultiAgentClientBinding root, int maxConcurrentSubagents)
    {
        public MultiAgentClientBinding Root { get; } = root;
        public int MaxConcurrentSubagents { get; } = maxConcurrentSubagents;
        public SemaphoreSlim ChildModels { get; } = new(maxConcurrentSubagents, maxConcurrentSubagents);
        public Dictionary<string, MultiAgentClientBinding> Bindings { get; } = new(StringComparer.Ordinal) { [root.Id] = root };
    }

    private sealed class ModelLease(SemaphoreSlim? semaphore) : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BindingManifest
    {
        public int Version { get; set; } = 1;
        public Guid Owner { get; set; }
        public string RootThreadId { get; set; } = string.Empty;
        public bool Persist { get; set; }
        public int MaxConcurrentSubagents { get; set; }
        public List<BindingEntry> Bindings { get; set; } = [];
    }

    private sealed class BindingEntry
    {
        public string Id { get; set; } = string.Empty;
        public string ThreadId { get; set; } = string.Empty;
        public string AgentName { get; set; } = string.Empty;
        public string ParentName { get; set; } = string.Empty;
        public string ParentThreadId { get; set; } = string.Empty;
        public string SpawnCallId { get; set; } = string.Empty;
        public bool SpawnFailed { get; set; }
        public bool SpawnCompleted { get; set; }
        public string RunSessionKey { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>
/// Stores Responses continuations independently of the legacy server agent runtime.
/// Client input owns the conversation; persisted records only resolve response references and retries.
/// </summary>
public sealed class NativeClientSessionStore(string directory)
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(directory), "native-client-v1");
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly ConcurrentDictionary<string, NativeClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeClientGroup> _groups = new(StringComparer.Ordinal);
    private bool _identitiesRestored;
    private readonly Dictionary<(Guid Owner, string Thread, string Response), StoredResponse> _memoryResponses = [];
    private readonly Dictionary<(Guid Owner, string Thread, string Replay), StoredResponse> _memoryReplays = [];
    private readonly Dictionary<string, NativeClientSnapshotPool> _snapshotPools = new(StringComparer.Ordinal);

    public async Task<NativeClientSession> ResolveAsync(Guid owner, MultiAgentClientIdentity identity,
        bool persist, int maxConcurrentSubagents, CancellationToken ct)
    {
        ValidateIdentity(identity);
        if (maxConcurrentSubagents < 1) throw new BadRequestException("max_concurrent_subagents must be positive.");
        await _indexGate.WaitAsync(ct);
        try
        {
            await RestoreIdentities(ct);
            var session = FindSession(owner, identity.ThreadId);
            if (session is not null)
            {
                if (session.RootThreadId != identity.RootThreadId || session.ParentThreadId != identity.ParentThreadId
                    || session.AgentName != identity.AgentName)
                    throw new BadRequestException("The client thread identity cannot change its root, parent, or agent name.");
                if (session.Persist != persist)
                    throw new BadRequestException("The client thread must keep its initial storage policy.");
                _groups[SessionKey(owner, identity.RootThreadId)].Validate(identity, persist, maxConcurrentSubagents);
                return session;
            }
            var root = identity.ParentThreadId.Length == 0 ? null : FindSession(owner, identity.RootThreadId);
            var parent = identity.ParentThreadId.Length == 0 ? null : FindSession(owner, identity.ParentThreadId);
            if (parent is not null && (parent.RootThreadId != identity.RootThreadId
                || identity.AgentName[..identity.AgentName.LastIndexOf('/')] != parent.AgentName))
                throw new BadRequestException("The child identity does not belong to the declared parent.");
            if (root is not null && root.Persist != persist)
                throw new BadRequestException("Client children must use their root's storage policy.");
            session = CreateSession(owner, identity, persist, root?.MaxConcurrentSubagents ?? maxConcurrentSubagents);
            if (persist) await WriteAtomic(SessionPath(session), new SessionDocument
            {
                Owner = owner, ThreadId = session.ThreadId, RootThreadId = session.RootThreadId,
                ParentThreadId = session.ParentThreadId, AgentName = session.AgentName,
                MaxConcurrentSubagents = session.MaxConcurrentSubagents
            }, ct);
            RegisterSession(session);
            return session;
        }
        finally { _indexGate.Release(); }
    }

    public async Task<NativeClientSession?> TryGetAsync(Guid owner, string threadId, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { await RestoreIdentities(ct); return FindSession(owner, threadId); }
        finally { _indexGate.Release(); }
    }

    public async Task<NativeClientRequestLease> AcquireRequestAsync(NativeClientSession session, CancellationToken ct)
    {
        EnsureOwned(session);
        await session.Gate.WaitAsync(ct);
        var lease = new NativeClientRequestLease(session, ct);
        lock (session.ActivityLock) session.Activity = lease;
        try
        {
            if (session.ParentThreadId.Length > 0)
            {
                await session.ChildModels.WaitAsync(lease.Token);
                lease.MarkChildPermitAcquired();
            }
            lease.Token.ThrowIfCancellationRequested();
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    public Task CancelRequestAsync(NativeClientSession session, string? expectedRequestId = null)
    {
        EnsureOwned(session);
        lock (session.ActivityLock)
            return session.Activity is { } active && (expectedRequestId is null || active.RequestId == expectedRequestId)
                ? active.CancelAsync() : Task.CompletedTask;
    }

    public async Task<NativeClientRequest> ProjectRequestAsync(NativeClientSession session, D request,
        string turnId, CancellationToken ct)
    {
        EnsureOwned(session);
        var replayKey = turnId.Length == 0 ? "" : Hash(turnId + "\n" + Canonical(request));
        await _indexGate.WaitAsync(ct);
        try
        {
            var replay = replayKey.Length == 0 ? null : await FindReplay(session, replayKey, ct);
            if (replay is not null)
                return new() { Session = session, Payload = WebSearchPayload.DeepCopyObject(replay.Payload),
                    ReplayKey = replayKey, ReplayResponse = WebSearchPayload.DeepCopyObject(replay.Response) };
            var previousId = JsonDictionaryValue.String(request, "previous_response_id");
            var payload = new D();
            var input = new List<object?>();
            if (previousId.Length > 0)
            {
                var previous = await ReadResponse(session, previousId, ct)
                    ?? throw new BadRequestException("The previous response is unavailable for this API key and client thread. Resend the full client history without previous_response_id.");
                // Responses instructions are request-scoped; tools and model settings may be omitted in a delta.
                foreach (var pair in previous.Payload.Where(p => p.Key is not ("input" or "instructions" or "previous_response_id")))
                    payload[pair.Key] = WebSearchPayload.DeepCopy(pair.Value);
                input.AddRange(JsonDictionaryValue.List(previous.Payload, "input").Select(WebSearchPayload.DeepCopy));
                var previousOutput = JsonDictionaryValue.List(previous.Response, "output");
                // Incomplete responses never published executable tools, so those partial calls
                // must not introduce unanswered tool calls into the continuation history.
                if (JsonDictionaryValue.String(previous.Response, "status") == "incomplete")
                    previousOutput = previousOutput.Where(i => i is not D item
                        || !(JsonDictionaryValue.String(item, "type") is "function_call" or "custom_tool_call"
                            || JsonDictionaryValue.String(item, "type") == "tool_search_call"
                            && JsonDictionaryValue.String(item, "execution") == "client")).ToList();
                input.AddRange(previousOutput.Select(WebSearchPayload.DeepCopy));
            }
            foreach (var pair in request) payload[pair.Key] = WebSearchPayload.DeepCopy(pair.Value);
            input.AddRange(ReadInput(request));
            payload["input"] = input;
            payload.Remove("previous_response_id");
            return new() { Session = session, Payload = payload, ReplayKey = replayKey };
        }
        finally { _indexGate.Release(); }
    }

    /// <summary>Call before publishing any executable tool completion or terminal success.</summary>
    public async Task CommitResponseAsync(NativeClientSession session, NativeClientRequest request,
        D response, CancellationToken ct)
    {
        EnsureOwned(session);
        if (!ReferenceEquals(request.Session, session)) throw new InvalidOperationException("The request belongs to another client session.");
        var responseId = JsonDictionaryValue.String(response, "id");
        var status = JsonDictionaryValue.String(response, "status");
        if (responseId.Length == 0 || status is not ("completed" or "incomplete"))
            throw new InvalidOperationException("Only a completed or incomplete response with an id can be committed.");
        var stored = new StoredResponse { Owner = session.Owner, ThreadId = session.ThreadId, ReplayKey = status == "completed" ? request.ReplayKey : "",
            Payload = WebSearchPayload.DeepCopyObject(request.Payload), Response = WebSearchPayload.DeepCopyObject(response) };
        await _indexGate.WaitAsync(ct);
        try
        {
            var existing = await ReadResponse(session, responseId, ct);
            if (existing is not null && (Canonical(existing.Payload) != Canonical(stored.Payload)
                || Canonical(existing.Response) != Canonical(stored.Response)))
                throw new InvalidOperationException("A response id cannot be reused for different content.");
            if (session.Persist)
            {
                var recordName = stored.ReplayKey.Length > 0 ? "turn-" + stored.ReplayKey : "response-" + Hash(responseId);
                // One atomic record contains the response and retry identity. The response-id file is a derived index.
                await WriteAtomic(RecordPath(session, recordName), stored, ct);
                await WriteAtomic(ResponsePath(session, responseId), recordName, ct);
            }
            else
            {
                var key = SessionKey(session.Owner, session.ThreadId);
                if (!_snapshotPools.TryGetValue(key, out var pool))
                    _snapshotPools[key] = pool = new NativeClientSnapshotPool();
                stored.Payload = pool.Snapshot(stored.Payload);
                stored.Response = pool.Snapshot(stored.Response);
                _memoryResponses[(session.Owner, session.ThreadId, responseId)] = stored;
                if (stored.ReplayKey.Length > 0) _memoryReplays[(session.Owner, session.ThreadId, stored.ReplayKey)] = stored;
            }
        }
        finally { _indexGate.Release(); }
    }

    private NativeClientSession CreateSession(Guid owner, MultiAgentClientIdentity identity, bool persist, int limit)
    {
        var groupKey = SessionKey(owner, identity.RootThreadId);
        if (!_groups.TryGetValue(groupKey, out var group) || group.Agents.Count == 0)
            _groups[groupKey] = group = new NativeClientGroup(persist, limit);
        group.Validate(identity, persist, limit);
        return new() { Owner = owner, ThreadId = identity.ThreadId, RootThreadId = identity.RootThreadId,
            ParentThreadId = identity.ParentThreadId, AgentName = identity.AgentName, Persist = persist,
            MaxConcurrentSubagents = group.Limit, ChildModels = group.ChildModels };
    }

    private void RegisterSession(NativeClientSession session)
    {
        _groups[SessionKey(session.Owner, session.RootThreadId)].Agents[session.AgentName] = session.ThreadId;
        _sessions.TryAdd(SessionKey(session.Owner, session.ThreadId), session);
    }

    private NativeClientSession? FindSession(Guid owner, string threadId)
        => _sessions.GetValueOrDefault(SessionKey(owner, threadId));

    private async Task RestoreIdentities(CancellationToken ct)
    {
        if (_identitiesRestored) return;
        // Identity documents are small and loaded once. Response payloads remain lazy.
        // Restoring the full index is necessary before accepting any new agent name.
        var documents = new List<SessionDocument>();
        if (Directory.Exists(_directory))
            foreach (var folder in Directory.EnumerateDirectories(_directory))
            {
                var path = Path.Combine(folder, "session.json");
                if (!File.Exists(path)) continue;
                await using var stream = File.OpenRead(path);
                var document = await JsonSerializer.DeserializeAsync<SessionDocument>(stream, cancellationToken: ct)
                    ?? throw new InvalidDataException("The native client identity is empty.");
                if (document.Version != 1 || document.MaxConcurrentSubagents < 1
                    || Path.GetFileName(folder) != Hash(SessionKey(document.Owner, document.ThreadId)))
                    throw new InvalidDataException("The native client identity is invalid.");
                documents.Add(document);
            }
        // Root policy takes precedence for historical documents; child requests always
        // inherit their established group limit, independent of disk enumeration order.
        foreach (var document in documents.OrderBy(d => d.AgentName.Count(c => c == '/')))
        {
            var identity = new MultiAgentClientIdentity { ThreadId = document.ThreadId, RootThreadId = document.RootThreadId,
                ParentThreadId = document.ParentThreadId, AgentName = document.AgentName };
            ValidateIdentity(identity);
            var session = CreateSession(document.Owner, identity, true, document.MaxConcurrentSubagents);
            RegisterSession(session);
        }
        _identitiesRestored = true;
    }

    private async Task<StoredResponse?> ReadResponse(NativeClientSession session, string responseId, CancellationToken ct)
    {
        if (!session.Persist) return _memoryResponses.GetValueOrDefault((session.Owner, session.ThreadId, responseId));
        var path = ResponsePath(session, responseId);
        if (!File.Exists(path)) return null;
        var recordName = JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(path, ct));
        if (recordName is null || recordName != Path.GetFileName(recordName))
            throw new InvalidDataException("The native response reference is invalid.");
        var stored = await ReadStored(RecordPath(session, recordName), ct);
        if (stored.Owner != session.Owner || stored.ThreadId != session.ThreadId
            || JsonDictionaryValue.String(stored.Response, "id") != responseId)
            throw new InvalidDataException("The native response identity is invalid.");
        return stored;
    }

    private async Task<StoredResponse?> FindReplay(NativeClientSession session, string replayKey, CancellationToken ct)
    {
        if (!session.Persist)
            return _memoryReplays.GetValueOrDefault((session.Owner, session.ThreadId, replayKey));
        var recordName = "turn-" + replayKey;
        var path = RecordPath(session, recordName);
        if (!File.Exists(path)) return null;
        var stored = await ReadStored(path, ct);
        if (stored.Owner != session.Owner || stored.ThreadId != session.ThreadId || stored.ReplayKey != replayKey)
            throw new InvalidDataException("The native replay identity is invalid.");
        // Recover the index if a crash occurred after the atomic record write and before index publication.
        var responseId = JsonDictionaryValue.String(stored.Response, "id");
        await WriteAtomic(ResponsePath(session, responseId), recordName, ct);
        return stored;
    }

    private static async Task<StoredResponse> ReadStored(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var value = (D)WebSearchPayload.FromJsonElement(json.RootElement)!;
        if (!json.RootElement.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var versionNumber) || versionNumber != 1
            || !Guid.TryParse(JsonDictionaryValue.String(value, "Owner"), out var owner))
            throw new InvalidDataException("The native response record is invalid.");
        return new() { Owner = owner, ThreadId = JsonDictionaryValue.String(value, "ThreadId"),
            ReplayKey = JsonDictionaryValue.String(value, "ReplayKey"),
            Payload = JsonDictionaryValue.Object(value, "Payload", WebSearchPayload.DeepCopyObject),
            Response = JsonDictionaryValue.Object(value, "Response", WebSearchPayload.DeepCopyObject) };
    }

    private static List<object?> ReadInput(D request)
    {
        var input = JsonDictionaryValue.Get(request, "input");
        if (input is null) return [];
        if (input is string text) return [new D { ["type"] = "message", ["role"] = "user", ["content"] = text }];
        if (input is not List<object?> list || list.Any(i => i is not IReadOnlyDictionary<string, object?>))
            throw new BadRequestException("input must be a string or an array of objects.");
        return list.Select(WebSearchPayload.DeepCopy).ToList();
    }

    private static void ValidateIdentity(MultiAgentClientIdentity identity)
    {
        if (identity.ThreadId.Length == 0 || identity.RootThreadId.Length == 0)
            throw new BadRequestException("Native client identity requires thread and root ids.");
        if (identity.ParentThreadId.Length == 0)
        {
            if (identity.RootThreadId != identity.ThreadId || identity.AgentName != "/root")
                throw new BadRequestException("A root client identity must own its thread.");
        }
        else if (identity.ThreadId == identity.ParentThreadId || identity.ThreadId == identity.RootThreadId
            || !identity.AgentName.StartsWith("/root/", StringComparison.Ordinal)
            || identity.AgentName[6..].Split('/').Any(p => p.Length == 0 || p.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            || (identity.ParentThreadId == identity.RootThreadId) != (identity.AgentName.Count(c => c == '/') == 2))
            throw new BadRequestException("The native child parent, root, and agent path are inconsistent.");
    }

    private void EnsureOwned(NativeClientSession session)
    {
        if (!_sessions.TryGetValue(SessionKey(session.Owner, session.ThreadId), out var known) || !ReferenceEquals(known, session))
            throw new InvalidOperationException("The native client session belongs to another store.");
    }

    private static string Canonical(D value) => NativeClientSnapshotPool.Canonical(value);

    private static async Task WriteAtomic(string path, object value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), ct);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string SessionKey(Guid owner, string thread) => owner.ToString("N") + ":" + thread;
    private string SessionDirectory(NativeClientSession session) => Path.Combine(_directory, Hash(SessionKey(session.Owner, session.ThreadId)));
    private string SessionPath(NativeClientSession session) => Path.Combine(SessionDirectory(session), "session.json");
    private string ResponsePath(NativeClientSession session, string id) => Path.Combine(SessionDirectory(session), "responses", Hash(id) + ".json");
    private string RecordPath(NativeClientSession session, string name) => Path.Combine(SessionDirectory(session), "records", name + ".json");
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class SessionDocument
    {
        public int Version { get; set; } = 1;
        public Guid Owner { get; set; }
        public string ThreadId { get; set; } = "";
        public string RootThreadId { get; set; } = "";
        public string ParentThreadId { get; set; } = "";
        public string AgentName { get; set; } = "";
        public int MaxConcurrentSubagents { get; set; }
    }

    private sealed class StoredResponse
    {
        public int Version { get; set; } = 1;
        public Guid Owner { get; set; }
        public string ThreadId { get; set; } = "";
        public string ReplayKey { get; set; } = "";
        public D Payload { get; set; } = [];
        public D Response { get; set; } = [];
    }
}

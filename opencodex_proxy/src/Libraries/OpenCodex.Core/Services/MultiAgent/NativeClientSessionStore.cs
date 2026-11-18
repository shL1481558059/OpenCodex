using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>
/// Stores Responses continuations independently of the legacy server agent runtime.
/// Client input owns the conversation; JSON files retain durable identity and response records.
/// </summary>
public sealed partial class NativeClientSessionStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly ConcurrentDictionary<string, NativeClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeClientGroup> _groups = new(StringComparer.Ordinal);
    private readonly NativeClientStateCache _cache;
    private readonly List<Func<CancellationToken, Task>> _cachePublishes = [];

    public NativeClientSessionStore(string directory, NativeClientCacheOptions? options = null,
        TimeProvider? timeProvider = null, NativeClientStateCache? cache = null)
    {
        _directory = Path.Combine(Path.GetFullPath(directory), "native-client-v1");
        _cache = cache ?? new NativeClientStateCache(null);
        InitializeResources(options, timeProvider);
    }

    public async Task<NativeClientSession> ResolveAsync(Guid owner, MultiAgentClientIdentity identity,
        int maxConcurrentSubagents, CancellationToken ct)
    {
        ValidateIdentity(identity);
        ValidateSize(identity);
        if (maxConcurrentSubagents < 1) throw new BadRequestException("max_concurrent_subagents must be positive.");
        await _indexGate.WaitAsync(ct);
        NativeClientSession? session = null;
        List<Func<CancellationToken, Task>> publishes = [];
        Exception? error = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Sweep();
            CheckIdleSession(FindSession(owner, identity.ThreadId));
            CheckIdleGroup(owner, identity.RootThreadId);
            await RestoreGroup(owner, identity.RootThreadId, ct, identity.ThreadId);
            session = FindSession(owner, identity.ThreadId);
            if (session is not null)
            {
                if (session.RootThreadId != identity.RootThreadId || session.ParentThreadId != identity.ParentThreadId
                    || session.AgentName != identity.AgentName)
                    throw new BadRequestException("The client thread identity cannot change its root, parent, or agent name.");
                var group = _groups[SessionKey(owner, identity.RootThreadId)];
                group.Validate(identity, maxConcurrentSubagents);
                EnsureBound(group, session);
                Touch(session);
            }
            else
            {
                var root = identity.ParentThreadId.Length == 0 ? null : FindSession(owner, identity.RootThreadId);
                var parent = identity.ParentThreadId.Length == 0 ? null : FindSession(owner, identity.ParentThreadId);
                if (parent is not null && (parent.RootThreadId != identity.RootThreadId
                    || identity.AgentName[..identity.AgentName.LastIndexOf('/')] != parent.AgentName))
                    throw new BadRequestException("The child identity does not belong to the declared parent.");
                if (_groups.TryGetValue(SessionKey(owner, identity.RootThreadId), out var existingGroup))
                {
                    existingGroup.Validate(identity, maxConcurrentSubagents);
                    if (existingGroup.Agents.Count >= _options.MaxGroupSessions) throw Quota("group member");
                }

                MakeRoom(owner, 1, identity.RootThreadId);
                session = CreateSession(owner, identity, root?.MaxConcurrentSubagents ?? maxConcurrentSubagents);
                GroupManifest? previousManifest = null;
                try
                {
                    previousManifest = await ReadGroupManifest(owner, identity.RootThreadId, ct);
                    var generation = NextGeneration(session.Group, previousManifest);
                    await WriteGroupManifest(session.Group, owner, identity.RootThreadId, generation,
                        Members(session.Group).Concat([session]).ToList(), ct);
                    await WriteAtomic(SessionPath(session), ToDocument(session), ct);
                    RegisterSession(session);
                    session.Group.Generation = generation;
                    QueueGroupPublish(session.Group);
                }
                catch
                {
                    session.Gate.Dispose();
                    await TryRevertGroupManifest(owner, identity.RootThreadId, previousManifest, ct);
                    var groupKey = SessionKey(owner, identity.RootThreadId);
                    if (_groups.TryGetValue(groupKey, out var failedGroup) && failedGroup.Agents.Count == 0)
                    {
                        _groups.Remove(groupKey);
                        failedGroup.ChildModels.Dispose();
                    }
                    throw;
                }
            }
        }
        catch (Exception exception) { error = exception; }
        finally
        {
            publishes = TakeCachePublishes();
            _indexGate.Release();
        }
        await FlushCachePublishes(publishes, ct);
        if (error is not null) throw error;
        return session!;
    }

    public async Task<NativeClientSession?> TryGetAsync(Guid owner, string threadId, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        NativeClientSession? found = null;
        List<Func<CancellationToken, Task>> publishes = [];
        Exception? error = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Sweep();
            CheckIdleSession(FindSession(owner, threadId));
            found = FindSession(owner, threadId);
            if (found is not null)
            {
                if (!TryEnsureBound(found, owner, threadId)) found = null;
                else Touch(found);
            }
            else
            {
                var document = await ReadIdentity(owner, threadId, ct);
                if (document is not null)
                {
                    await RestoreGroup(owner, document.RootThreadId, ct, threadId);
                    found = FindSession(owner, threadId);
                    if (found is null || !TryEnsureBound(found, owner, threadId))
                    {
                        await RestoreGroup(owner, document.RootThreadId, ct, threadId, forceDisk: true);
                        found = FindSession(owner, threadId);
                    }
                    if (found is null || !TryEnsureBound(found, owner, threadId)) found = null;
                    else Touch(found);
                }
            }
        }
        catch (Exception exception) { error = exception; }
        finally
        {
            publishes = TakeCachePublishes();
            _indexGate.Release();
        }
        await FlushCachePublishes(publishes, ct);
        if (error is not null) throw error;
        return found;
    }

    public async Task<NativeClientRequestLease> AcquireRequestAsync(NativeClientSession session, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Sweep();
            CheckIdleSession(session);
            EnsureOwned(session);
            if (session.RequestReferences >= _options.MaxWaitersPerSession + 1L) throw Quota("session waiter");
            Interlocked.Increment(ref session.RequestReferences);
            Touch(session);
        }
        finally { _indexGate.Release(); }

        try { await session.Gate.WaitAsync(ct); }
        catch { ReleaseReference(session); throw; }
        var lease = new NativeClientRequestLease(session, ct, () => ReleaseReference(session));
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
        var replayKey = turnId.Length == 0 ? "" : Hash(turnId + "\n" + JsonHash(request));
        await _indexGate.WaitAsync(ct);
        try
        {
            Sweep();
            EnsureOwned(session);
            Touch(session);
            var replay = replayKey.Length == 0 ? null : await FindReplay(session, replayKey, ct);
            if (replay is not null)
                return new()
                {
                    Session = session,
                    ReplayKey = replayKey,
                    ReplayResponse = replay.Response,
                    PayloadLoader = () => ReadReplayPayload(RecordPath(session, "turn-" + replayKey))
                };

            var previousId = JsonDictionaryValue.String(request, "previous_response_id");
            var payload = new D();
            var input = new List<object?>();
            if (previousId.Length > 0)
            {
                var previous = await ReadResponse(session, previousId, ct)
                    ?? throw new BadRequestException("The previous response is unavailable for this API key and client thread. Resend the full client history without previous_response_id.");
                foreach (var pair in previous.Payload.Where(p => p.Key is not ("input" or "instructions" or "previous_response_id")))
                    payload[pair.Key] = pair.Value;
                input.AddRange(JsonDictionaryValue.List(previous.Payload, "input"));
                var previousOutput = JsonDictionaryValue.List(previous.Response, "output");
                if (JsonDictionaryValue.String(previous.Response, "status") == "incomplete")
                    previousOutput = previousOutput.Where(i => i is not D item
                        || !(JsonDictionaryValue.String(item, "type") is "function_call" or "custom_tool_call"
                            || JsonDictionaryValue.String(item, "type") == "tool_search_call"
                            && JsonDictionaryValue.String(item, "execution") == "client")).ToList();
                input.AddRange(previousOutput);
            }

            foreach (var pair in request)
            {
                if (pair.Key is not ("input" or "previous_response_id")) payload[pair.Key] = pair.Value;
            }
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
        if (!ReferenceEquals(request.Session, session))
            throw new InvalidOperationException("The request belongs to another client session.");
        var responseId = JsonDictionaryValue.String(response, "id");
        var status = JsonDictionaryValue.String(response, "status");
        if (responseId.Length == 0 || status is not ("completed" or "incomplete"))
            throw new InvalidOperationException("Only a completed or incomplete response with an id can be committed.");

        var stored = new StoredResponse
        {
            Owner = session.Owner,
            ThreadId = session.ThreadId,
            ResponseId = responseId,
            ReplayKey = status == "completed" ? request.ReplayKey : "",
            Payload = request.Payload,
            Response = response
        };

        await _indexGate.WaitAsync(ct);
        try
        {
            Sweep();
            EnsureOwned(session);
            Touch(session);
            var existing = await ReadResponse(session, responseId, ct);
            if (existing is not null && (JsonHash(existing.Payload) != JsonHash(stored.Payload)
                || JsonHash(existing.Response) != JsonHash(stored.Response)))
                throw new InvalidOperationException("A response id cannot be reused for different content.");

            var recordName = stored.ReplayKey.Length > 0 ? "turn-" + stored.ReplayKey : "response-" + Hash(responseId);
            await WriteStoredResponse(session, recordName, stored, ct);
            await WriteAtomic(ResponsePath(session, responseId), recordName, ct);
        }
        finally { _indexGate.Release(); }

    }

    private NativeClientSession CreateSession(Guid owner, MultiAgentClientIdentity identity, int limit)
    {
        var groupKey = SessionKey(owner, identity.RootThreadId);
        if (!_groups.TryGetValue(groupKey, out var group) || group.Agents.Count == 0)
            _groups[groupKey] = group = new NativeClientGroup(limit, owner, identity.RootThreadId) { LastAccess = Now };
        group.Validate(identity, limit);
        return new()
        {
            Owner = owner,
            ThreadId = identity.ThreadId,
            RootThreadId = identity.RootThreadId,
            ParentThreadId = identity.ParentThreadId,
            AgentName = identity.AgentName,
            MaxConcurrentSubagents = group.Limit,
            ChildModels = group.ChildModels,
            Group = group,
            LastAccess = Now
        };
    }

    private void RegisterSession(NativeClientSession session)
    {
        _groups[SessionKey(session.Owner, session.RootThreadId)].Agents[session.AgentName] = session.ThreadId;
        if (_sessions.TryAdd(SessionKey(session.Owner, session.ThreadId), session))
            _tenantSessionCounts[session.Owner] = _tenantSessionCounts.GetValueOrDefault(session.Owner) + 1;
        Touch(session);
    }

    private NativeClientSession? FindSession(Guid owner, string threadId)
        => _sessions.GetValueOrDefault(SessionKey(owner, threadId));

    private async Task<SessionDocument?> ReadIdentity(Guid owner, string thread, CancellationToken ct)
    {
        var path = Path.Combine(_directory, Hash(SessionKey(owner, thread)), "session.json");
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<SessionDocument>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("The native client identity is empty.");
        ValidateDocument(owner, thread, document, document.MaxConcurrentSubagents);
        QueueCachePublish(token => _cache.SetSessionAsync(ToCachedSession(owner, document), token));
        return document;
    }

    private async Task RestoreGroup(Guid owner, string rootThread, CancellationToken ct,
        string? requiredThread = null, bool forceDisk = false)
    {
        var groupKey = SessionKey(owner, rootThread);
        if (_groups.ContainsKey(groupKey) && !forceDisk
            && (requiredThread is null || FindSession(owner, requiredThread) is not null))
            return;

        if (_groups.ContainsKey(groupKey) && requiredThread is not null
            && FindSession(owner, requiredThread) is null
            && !SessionFileExists(owner, requiredThread))
            return;

        if (_groups.TryGetValue(groupKey, out var existing) && !HasReferences(existing))
            RemoveGroup(groupKey, existing);

        var manifest = await ReadGroupManifest(owner, rootThread, ct);
        var cached = forceDisk ? null : await ReadTrustedCachedGroup(owner, rootThread, manifest, requiredThread, ct);
        var documents = cached
            ?? await ReadManifestGroup(owner, rootThread, manifest, requiredThread, ct)
            ?? await ReadGroupFromDisk(owner, rootThread, ct);
        if (requiredThread is not null && documents.All(d => d.ThreadId != requiredThread)
            && cached is not null)
        {
            QueueCachePublish(token => _cache.RemoveGroupAsync(owner, rootThread, token));
            documents = await ReadManifestGroup(owner, rootThread, manifest, requiredThread, ct)
                ?? await ReadGroupFromDisk(owner, rootThread, ct);
        }
        if (documents.Count == 0) return;
        RejectDuplicateAgents(documents);
        var missing = documents.Where(d => FindSession(owner, d.ThreadId) is null).ToList();
        if (missing.Count == 0)
        {
            if (_groups.TryGetValue(groupKey, out var loaded))
                QueueGroupPublish(loaded);
            return;
        }
        if (!_groups.ContainsKey(groupKey)) MakeRoom(owner, documents.Count);
        try
        {
            NativeClientGroup? group = _groups.GetValueOrDefault(groupKey);
            foreach (var document in missing.OrderBy(d => d.AgentName.Count(c => c == '/')))
            {
                var identity = ToIdentity(document);
                var session = CreateSession(owner, identity, document.MaxConcurrentSubagents);
                RegisterSession(session);
                group = session.Group;
                QueueCachePublish(token => _cache.SetSessionAsync(ToCachedSession(owner, document), token));
            }
            if (group is null) return;
            if (manifest is { Generation: > 0 } && SameThreads(manifest.Sessions, documents))
                group.Generation = manifest.Generation;
            else
            {
                var generation = Math.Max(manifest?.Generation ?? 0, group.Generation) + 1;
                if (generation < 1) generation = 1;
                await WriteGroupManifest(group, owner, rootThread, generation, Members(group), ct);
                group.Generation = generation;
            }
            QueueGroupPublish(group);
        }
        catch
        {
            if (_groups.TryGetValue(groupKey, out var failed))
                RemoveGroup(groupKey, failed);
            throw;
        }
    }

    private async Task<List<SessionDocument>?> ReadTrustedCachedGroup(Guid owner, string rootThread,
        GroupManifest? manifest, string? requiredThread, CancellationToken ct)
    {
        var cached = await _cache.GetGroupAsync(owner, rootThread, ct);
        if (cached is null) return null;
        if (manifest is null || cached.Generation <= 0 || cached.Generation != manifest.Generation
            || cached.MaxConcurrentSubagents < 1 || cached.Sessions.Count == 0
            || !SameThreads(cached.Sessions.Select(ToDocument), manifest.Sessions)
            || (requiredThread is not null && cached.Sessions.All(s => s.ThreadId != requiredThread)))
        {
            QueueCachePublish(token => _cache.RemoveGroupAsync(owner, rootThread, token));
            return null;
        }

        var documents = new List<SessionDocument>(cached.Sessions.Count);
        foreach (var session in cached.Sessions)
        {
            if (session.RootThreadId != rootThread)
            {
                QueueCachePublish(token => _cache.RemoveGroupAsync(owner, rootThread, token));
                return null;
            }
            var document = ToDocument(session);
            try { ValidateDocument(owner, session.ThreadId, document, cached.MaxConcurrentSubagents); }
            catch (Exception exception) when (exception is InvalidDataException or BadRequestException)
            {
                QueueCachePublish(token => _cache.RemoveGroupAsync(owner, rootThread, token));
                return null;
            }
            documents.Add(document);
        }
        QueueCachePublish(token => _cache.TouchGroupAsync(owner, rootThread, token));
        return documents;
    }

    private async Task<List<SessionDocument>?> ReadManifestGroup(Guid owner, string rootThread,
        GroupManifest? manifest, string? requiredThread, CancellationToken ct)
    {
        if (manifest is null || manifest.Generation <= 0 || manifest.Sessions.Count == 0) return null;
        var documents = new List<SessionDocument>(manifest.Sessions.Count);
        foreach (var document in manifest.Sessions)
        {
            if (document.RootThreadId != rootThread) return null;
            var path = Path.Combine(_directory, Hash(SessionKey(owner, document.ThreadId)), "session.json");
            if (!File.Exists(path)) continue;
            try { ValidateDocument(owner, document.ThreadId, document, manifest.MaxConcurrentSubagents); }
            catch (Exception exception) when (exception is InvalidDataException or BadRequestException)
            {
                return null;
            }
            documents.Add(document);
        }
        if (documents.Count == 0) return null;
        if (requiredThread is not null && documents.All(d => d.ThreadId != requiredThread)) return null;
        return documents;
    }

    private async Task<List<SessionDocument>> ReadGroupFromDisk(Guid owner, string rootThread, CancellationToken ct)
    {
        var documents = new List<SessionDocument>();
        if (!Directory.Exists(_directory)) return documents;
        foreach (var folder in Directory.EnumerateDirectories(_directory))
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(folder, "session.json");
            if (!File.Exists(path)) continue;
            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<SessionDocument>(stream, cancellationToken: ct)
                ?? throw new InvalidDataException("The native client identity is empty.");
            if (document.Owner != owner || document.RootThreadId != rootThread) continue;
            ValidateDocument(owner, document.ThreadId, document, document.MaxConcurrentSubagents);
            if (Path.GetFileName(folder) != Hash(SessionKey(owner, document.ThreadId)))
                throw new InvalidDataException("The native client identity path is invalid.");
            if (documents.Count >= _options.MaxGroupSessions) throw Quota("persisted group member");
            documents.Add(document);
        }
        return documents;
    }

    private static void ValidateDocument(Guid owner, string requestedThread, SessionDocument document, int groupLimit)
    {
        if (document.Version != 1 || document.Owner != owner || document.ThreadId != requestedThread
            || document.MaxConcurrentSubagents < 1 || groupLimit < 1)
            throw new InvalidDataException("The native client identity is invalid.");
        ValidateIdentity(ToIdentity(document));
    }

    private async Task<StoredResponse?> ReadResponse(NativeClientSession session, string responseId, CancellationToken ct)
    {
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
        var recordName = "turn-" + replayKey;
        var path = RecordPath(session, recordName);
        if (!File.Exists(path)) return null;
        var stored = await ReadReplayRecord(path, ct);
        if (stored.Owner != session.Owner || stored.ThreadId != session.ThreadId || stored.ReplayKey != replayKey)
            throw new InvalidDataException("The native replay identity is invalid.");
        var responseId = JsonDictionaryValue.String(stored.Response, "id");
        await WriteAtomic(ResponsePath(session, responseId), recordName, ct);
        return stored;
    }

    private static async Task<StoredResponse> ReadReplayRecord(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Version", out var version)
            || !version.TryGetInt32(out var versionNumber))
            throw new InvalidDataException("The native replay record is invalid.");
        if (versionNumber == 1)
            return await ReadStored(path, ct);
        if (versionNumber != 2)
            throw new InvalidDataException("The native replay record version is unsupported.");

        var manifest = JsonSerializer.Deserialize<StoredResponseManifest>(root.GetRawText())
            ?? throw new InvalidDataException("The native replay manifest is invalid.");
        if (manifest.Owner == Guid.Empty || manifest.ResponseFile.Length == 0
            || manifest.ResponseFile != Path.GetFileName(manifest.ResponseFile))
            throw new InvalidDataException("The native replay manifest is invalid.");
        var response = await ReadStoredJson(
            Path.Combine(Path.GetDirectoryName(path)!, manifest.ResponseFile), ct);
        var responseId = JsonDictionaryValue.String(response, "id");
        if (responseId != manifest.ResponseId)
            throw new InvalidDataException("The native replay manifest identity is invalid.");
        return new()
        {
            Owner = manifest.Owner,
            ThreadId = manifest.ThreadId,
            ReplayKey = manifest.ReplayKey,
            ResponseId = responseId,
            Payload = [],
            Response = response
        };
    }

    private static D ReadReplayPayload(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Version", out var version)
            || !version.TryGetInt32(out var versionNumber))
            throw new InvalidDataException("The native replay record is invalid.");
        if (versionNumber == 1)
            return JsonDictionaryValue.Object(
                (D)WebSearchPayload.FromJsonElement(root)!, "Payload", static value => (D)value);
        if (versionNumber != 2)
            throw new InvalidDataException("The native replay record version is unsupported.");
        var manifest = JsonSerializer.Deserialize<StoredResponseManifest>(root.GetRawText())
            ?? throw new InvalidDataException("The native replay manifest is invalid.");
        if (manifest.PayloadFile.Length == 0 || manifest.PayloadFile != Path.GetFileName(manifest.PayloadFile))
            throw new InvalidDataException("The native replay manifest is invalid.");
        var payloadPath = Path.Combine(Path.GetDirectoryName(path)!, manifest.PayloadFile);
        using var payloadDocument = JsonDocument.Parse(File.ReadAllBytes(payloadPath));
        if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The native response payload is invalid.");
        return (D)WebSearchPayload.FromJsonElement(payloadDocument.RootElement)!;
    }

    private static async Task<StoredResponse> ReadStored(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;
        if (!root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var versionNumber)
            || root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The native response record is invalid.");
        if (versionNumber == 2)
        {
            var manifest = JsonSerializer.Deserialize<StoredResponseManifest>(root.GetRawText())
                ?? throw new InvalidDataException("The native response manifest is invalid.");
            if (manifest.Owner == Guid.Empty
                || manifest.PayloadFile.Length == 0 || manifest.ResponseFile.Length == 0
                || manifest.PayloadFile != Path.GetFileName(manifest.PayloadFile)
                || manifest.ResponseFile != Path.GetFileName(manifest.ResponseFile))
                throw new InvalidDataException("The native response manifest is invalid.");
            var payload = await ReadStoredJson(Path.Combine(Path.GetDirectoryName(path)!, manifest.PayloadFile), ct);
            var response = await ReadStoredJson(Path.Combine(Path.GetDirectoryName(path)!, manifest.ResponseFile), ct);
            var responseId = JsonDictionaryValue.String(response, "id");
            if (responseId != manifest.ResponseId)
                throw new InvalidDataException("The native response manifest identity is invalid.");
            return new()
            {
                Owner = manifest.Owner,
                ThreadId = manifest.ThreadId,
                ReplayKey = manifest.ReplayKey,
                ResponseId = responseId,
                Payload = payload,
                Response = response
            };
        }

        if (versionNumber != 1)
            throw new InvalidDataException("The native response record version is unsupported.");
        var value = (D)WebSearchPayload.FromJsonElement(root)!;
        if (!Guid.TryParse(JsonDictionaryValue.String(value, "Owner"), out var legacyOwner))
            throw new InvalidDataException("The native response record is invalid.");
        var legacyResponse = JsonDictionaryValue.Object(value, "Response", static data => (D)data);
        return new()
        {
            Owner = legacyOwner,
            ThreadId = JsonDictionaryValue.String(value, "ThreadId"),
            ReplayKey = JsonDictionaryValue.String(value, "ReplayKey"),
            ResponseId = JsonDictionaryValue.String(legacyResponse, "id"),
            Payload = JsonDictionaryValue.Object(value, "Payload", static data => (D)data),
            Response = legacyResponse
        };
    }

    private static async Task<D> ReadStoredJson(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The native response payload is invalid.");
        return (D)WebSearchPayload.FromJsonElement(json.RootElement)!;
    }

    private static List<object?> ReadInput(D request)
    {
        var input = JsonDictionaryValue.Get(request, "input");
        if (input is null) return [];
        if (input is string text) return [new D { ["type"] = "message", ["role"] = "user", ["content"] = text }];
        if (input is not List<object?> list || list.Any(i => i is not IReadOnlyDictionary<string, object?>))
            throw new BadRequestException("input must be a string or an array of objects.");
        return list;
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
            throw new InvalidOperationException("The native client session is no longer active.");
    }

    private async Task CacheSessionAsync(NativeClientSession session, CancellationToken ct)
        => await _cache.SetSessionAsync(new()
        {
            Owner = session.Owner,
            ThreadId = session.ThreadId,
            RootThreadId = session.RootThreadId,
            ParentThreadId = session.ParentThreadId,
            AgentName = session.AgentName,
            MaxConcurrentSubagents = session.MaxConcurrentSubagents
        }, ct);

    private async Task CacheGroupAsync(Guid owner, string rootThread, CancellationToken ct)
    {
        if (!_groups.TryGetValue(SessionKey(owner, rootThread), out var group)) return;
        var sessions = group.Agents.Values.Select(thread => FindSession(owner, thread)).OfType<NativeClientSession>()
            .Select(session => new NativeClientCachedSession
            {
                Owner = session.Owner,
                ThreadId = session.ThreadId,
                RootThreadId = session.RootThreadId,
                ParentThreadId = session.ParentThreadId,
                AgentName = session.AgentName,
                MaxConcurrentSubagents = session.MaxConcurrentSubagents
            }).ToList();
        await _cache.SetGroupAsync(new()
        {
            Owner = owner,
            RootThreadId = rootThread,
            Generation = group.Generation,
            MaxConcurrentSubagents = group.Limit,
            Sessions = sessions
        }, ct);
    }

    private static SessionDocument ToDocument(NativeClientSession session) => new()
    {
        Owner = session.Owner,
        ThreadId = session.ThreadId,
        RootThreadId = session.RootThreadId,
        ParentThreadId = session.ParentThreadId,
        AgentName = session.AgentName,
        MaxConcurrentSubagents = session.MaxConcurrentSubagents
    };

    private static SessionDocument ToDocument(NativeClientCachedSession session) => new()
    {
        Owner = session.Owner,
        ThreadId = session.ThreadId,
        RootThreadId = session.RootThreadId,
        ParentThreadId = session.ParentThreadId,
        AgentName = session.AgentName,
        MaxConcurrentSubagents = session.MaxConcurrentSubagents
    };

    private static NativeClientCachedSession ToCachedSession(Guid owner, SessionDocument document) => new()
    {
        Owner = owner,
        ThreadId = document.ThreadId,
        RootThreadId = document.RootThreadId,
        ParentThreadId = document.ParentThreadId,
        AgentName = document.AgentName,
        MaxConcurrentSubagents = document.MaxConcurrentSubagents
    };

    private static MultiAgentClientIdentity ToIdentity(SessionDocument document) => new()
    {
        ThreadId = document.ThreadId,
        RootThreadId = document.RootThreadId,
        ParentThreadId = document.ParentThreadId,
        AgentName = document.AgentName
    };

    private static string JsonHash(D value) => NativeClientSnapshotPool.Hash(value);

    private void QueueCachePublish(Func<CancellationToken, Task> work) => _cachePublishes.Add(work);

    private List<Func<CancellationToken, Task>> TakeCachePublishes()
    {
        var work = _cachePublishes.ToList();
        _cachePublishes.Clear();
        return work;
    }

    private void QueueGroupPublish(NativeClientGroup group)
        => QueueCachePublish(token => CacheGroupAsync(group.Owner, group.RootThreadId, token));

    private static async Task FlushCachePublishes(IReadOnlyList<Func<CancellationToken, Task>> work, CancellationToken ct)
    {
        foreach (var publish in work)
        {
            ct.ThrowIfCancellationRequested();
            await publish(ct);
        }
    }

    private static long NextGeneration(NativeClientGroup group, GroupManifest? manifest)
        => Math.Max(group.Generation, manifest?.Generation ?? 0) + 1;

    private bool TryEnsureBound(NativeClientSession session, Guid owner, string threadId)
    {
        try { EnsureBound(session.Group, session, owner, threadId); return true; }
        catch (Exception exception) when (exception is InvalidDataException or BadRequestException)
        {
            return false;
        }
    }

    private void EnsureBound(NativeClientGroup group, NativeClientSession session, Guid? owner = null, string? threadId = null)
    {
        if (owner is { } expectedOwner && session.Owner != expectedOwner)
            throw new InvalidDataException("The native client identity is invalid.");
        if (threadId is not null && session.ThreadId != threadId)
            throw new InvalidDataException("The native client identity is invalid.");
        if (!ReferenceEquals(session.Group, group) || group.Owner != session.Owner
            || group.RootThreadId != session.RootThreadId)
            throw new InvalidDataException("The native client identity is invalid.");
        if (!group.Agents.TryGetValue(session.AgentName, out var bound) || bound != session.ThreadId)
            throw new InvalidDataException("The native client identity is invalid.");
        if (session.ParentThreadId.Length == 0) return;
        var parent = FindSession(session.Owner, session.ParentThreadId);
        if (parent is null) return;
        if (parent.Owner != session.Owner || parent.RootThreadId != session.RootThreadId
            || parent.AgentName != session.AgentName[..session.AgentName.LastIndexOf('/')])
            throw new InvalidDataException("The native client identity is invalid.");
    }

    private static void RejectDuplicateAgents(IEnumerable<SessionDocument> documents)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var threads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (!names.Add(document.AgentName) || !threads.Add(document.ThreadId))
                throw new InvalidDataException("The native client group contains conflicting identities.");
        }
    }

    private static bool SameThreads(IEnumerable<SessionDocument> left, IEnumerable<SessionDocument> right)
    {
        var first = left.Select(d => d.ThreadId).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var second = right.Select(d => d.ThreadId).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        return first.SequenceEqual(second, StringComparer.Ordinal);
    }

    private bool SessionFileExists(Guid owner, string thread)
        => File.Exists(Path.Combine(_directory, Hash(SessionKey(owner, thread)), "session.json"));

    private string GroupManifestPath(Guid owner, string rootThread)
        => Path.Combine(_directory, "groups", Hash(SessionKey(owner, rootThread)), "group.json");

    private async Task<GroupManifest?> ReadGroupManifest(Guid owner, string rootThread, CancellationToken ct)
    {
        var path = GroupManifestPath(owner, rootThread);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<GroupManifest>(stream, cancellationToken: ct);
            if (manifest is null || manifest.Version != 1 || manifest.Generation <= 0
                || manifest.Owner != owner || manifest.RootThreadId != rootThread
                || manifest.MaxConcurrentSubagents < 1)
                return null;
            foreach (var document in manifest.Sessions)
            {
                try { ValidateDocument(owner, document.ThreadId, document, manifest.MaxConcurrentSubagents); }
                catch (Exception exception) when (exception is InvalidDataException or BadRequestException)
                {
                    return null;
                }
            }
            return manifest;
        }
        catch (JsonException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    private async Task WriteGroupManifest(NativeClientGroup group, Guid owner, string rootThread, long generation,
        IReadOnlyList<NativeClientSession> sessions, CancellationToken ct)
    {
        var manifest = new GroupManifest
        {
            Version = 1,
            Generation = generation,
            Owner = owner,
            RootThreadId = rootThread,
            MaxConcurrentSubagents = group.Limit,
            Sessions = sessions.Select(ToDocument).ToList()
        };
        await WriteAtomic(GroupManifestPath(owner, rootThread), manifest, ct);
    }

    private async Task TryRevertGroupManifest(Guid owner, string rootThread, GroupManifest? previous, CancellationToken ct)
    {
        try
        {
            var path = GroupManifestPath(owner, rootThread);
            if (previous is null)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            await WriteAtomic(path, previous, ct);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class GroupManifest
    {
        public int Version { get; set; } = 1;
        public long Generation { get; set; }
        public Guid Owner { get; set; }
        public string RootThreadId { get; set; } = "";
        public int MaxConcurrentSubagents { get; set; }
        public List<SessionDocument> Sessions { get; set; } = [];
    }


    private static async Task WriteAtomic(string path, object value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                BufferSize = 16 * 1024
            }))
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    ProxyAuditSpool.WriteJson(writer, value, 0);
                    writer.Flush();
                }
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }

            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task WriteStoredResponse(
        NativeClientSession session,
        string recordName,
        StoredResponse response,
        CancellationToken ct)
    {
        var payloadFile = recordName + ".payload.json";
        var responseFile = recordName + ".response.json";
        var directory = Path.Combine(SessionDirectory(session), "records");
        await WriteAtomic(Path.Combine(directory, payloadFile), response.Payload, ct);
        await WriteAtomic(Path.Combine(directory, responseFile), response.Response, ct);
        await WriteAtomic(RecordPath(session, recordName), new StoredResponseManifest
        {
            Owner = response.Owner,
            ThreadId = response.ThreadId,
            ResponseId = response.ResponseId,
            ReplayKey = response.ReplayKey,
            PayloadFile = payloadFile,
            ResponseFile = responseFile
        }, ct);
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
        public int Version { get; set; } = 2;
        public Guid Owner { get; set; }
        public string ThreadId { get; set; } = "";
        public string ReplayKey { get; set; } = "";
        public string ResponseId { get; set; } = "";
        public D Payload { get; set; } = [];
        public D Response { get; set; } = [];
    }

    private sealed class StoredResponseManifest
    {
        public int Version { get; set; } = 2;
        public Guid Owner { get; set; }
        public string ThreadId { get; set; } = "";
        public string ResponseId { get; set; } = "";
        public string ReplayKey { get; set; } = "";
        public string PayloadFile { get; set; } = "";
        public string ResponseFile { get; set; } = "";
    }
}

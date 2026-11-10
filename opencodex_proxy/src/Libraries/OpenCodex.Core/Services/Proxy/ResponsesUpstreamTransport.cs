using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>Selects HTTP or Responses WebSocket for a streaming upstream request.</summary>
public sealed class ResponsesUpstreamTransport : IDisposable
{
    private readonly IUpstreamClient http;
    private readonly ResponsesWebSocketPool sockets;
    private readonly ResponsesContextCacheOptions _options;
    private readonly TimeProvider _time;
    private readonly ITimer _cleanup;
    private readonly RefCountedKeyedLock _conversationLocks;
    private readonly Dictionary<string,long> _tenantBytes=new(StringComparer.Ordinal);
    private readonly object _probeGate=new();
    private long _snapshotBytes;
    public ResponsesUpstreamTransport(IUpstreamClient http,ResponsesWebSocketPool sockets,ILogger<ResponsesUpstreamTransport> logger,
        ResponsesContextCacheOptions? cacheOptions=null,TimeProvider? timeProvider=null)
    {
        this.http=http;this.sockets=sockets;_logger=logger;
        _options=(cacheOptions??new()).CopyValidated();_time=timeProvider??TimeProvider.System;
        _conversationLocks=new(_options.MaxConversationLocks,_options.MaxWaitersPerConversation);
        _cleanup=_time.CreateTimer(_=>CleanupExpired(),null,TimeSpan.FromSeconds(_options.CleanupIntervalSeconds),TimeSpan.FromSeconds(_options.CleanupIntervalSeconds));
    }
    public void Dispose()=>_cleanup.Dispose();
    internal (long Bytes,int Snapshots,int Tenants,int Locks,int Probes) CacheUsage
    { get { lock(_storeGate)return (_snapshotBytes,_responses.Count,_tenantBytes.Count,_conversationLocks.Count,_probes.Count); } }
    private static readonly string[] ComparisonIgnoredKeys =
    [
        "input", "previous_response_id", "stream", "stream_options", "background", "type", "client_metadata"
    ];
    private readonly object _storeGate = new();
    private readonly Dictionary<string, ContextSnapshot> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContextSnapshot> _latest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProbeState> _probes = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public async IAsyncEnumerable<string> StreamAsync(
        ProxyStreamContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in StreamEventsAsync(context, cancellationToken)) yield return frame.WireText;
    }

    public async IAsyncEnumerable<SseFrame> StreamEventsAsync(
        ProxyStreamContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var mode = ReadMode(context.Route.Channel);
        if (!CanUseWebSocket(context, mode))
        {
            LogTransport(context, mode, "http", null, UnavailableReason(context, mode));
            await foreach (var line in HttpStreamAsync(context, context.UpstreamRequest, cancellationToken)) yield return line;
            yield break;
        }

        var identity = ProxyConversationIdentity.From(context.RequestMetadata.Headers, context.Payload);
        var conversation = identity.ConversationKey;
        if (conversation is null)
        {
            LogTransport(context, mode, "http", null, "conversation_missing");
            await foreach (var line in HttpStreamAsync(context, context.UpstreamRequest, cancellationToken)) yield return line;
            yield break;
        }

        var owner = context.ApiKeyId?.ToString("N") ?? context.OwnerUsername;
        var fingerprint = ResponsesWebSocketLease.Fingerprint(context.Route.Channel);
        var conversationScope = owner + ":" + conversation;
        var contextScope = JsonSerializer.Serialize(new[] { owner,conversation,context.ChannelId,context.UpstreamModel,fingerprint });
        var probeKey = fingerprint + ":" + context.UpstreamModel;
        if (mode == "auto" && _probes.TryGetValue(probeKey, out var probe)
            && probe.ExpiresAt > _time.GetUtcNow() && !probe.Supported)
        {
            LogTransport(context, mode, "http", null, "probe_unsupported");
            await foreach (var line in HttpStreamAsync(context, context.UpstreamRequest, cancellationToken)) yield return line;
            yield break;
        }

        var conversationLease=await _conversationLocks.AcquireAsync(conversationScope,cancellationToken);
        try
        {
            var passthrough = mode == "passthrough";
            var planning = passthrough
                ? new StreamPlan(
                    context.UpstreamRequest,
                    PassthroughRequest(context),
                    JsonDictionaryValue.String(context.OriginalPayload, "previous_response_id"))
                : PlanContextRequest(contextScope, context.UpstreamRequest);
            if (planning is null)
            {
                if (mode == "context_pool")
                    throw new UpstreamException(
                        "The Responses context is unavailable on this proxy instance. Resend the full history without previous_response_id.",
                        ProxyHttpStatus.Conflict);
                LogTransport(context, mode, "http", null, "context_snapshot_missing");
                await foreach (var line in HttpStreamAsync(context, context.UpstreamRequest, cancellationToken)) yield return line;
                yield break;
            }

            var started = await TryStartAsync(
                context,
                planning.Outgoing,
                conversation,
                cancellationToken);
            if (started is null)
            {
                if (mode == "auto") MarkProbe(probeKey, false);
                if (mode == "passthrough") throw new UpstreamException(
                    "upstream Responses WebSocket is unavailable",
                    ProxyHttpStatus.BadGateway);
                if (mode == "context_pool") throw new UpstreamException(
                    "context_pool requires an available upstream Responses WebSocket",
                    ProxyHttpStatus.BadGateway);
                LogTransport(context, mode, "http", planning.Outgoing, "websocket_unavailable");
                await foreach (var line in HttpStreamAsync(
                    context,
                    FullReplay(planning.Logical),
                    cancellationToken)) yield return line;
                yield break;
            }

            if (started.First is not null && IsRecoverableSocketLoss(started.First) && !passthrough)
            {
                await started.DisposeAsync();
                var full = FullReplay(planning.Logical);
                started = await StartFullReplayAsync(context, full, conversation, cancellationToken);
                _logger.LogInformation(
                    "Responses upstream context recovered with a full replay. channel_id={ChannelId} model={Model} previous_response_id={PreviousResponseId}",
                    context.ChannelId,
                    context.UpstreamModel,
                    planning.PreviousId);
            }
            if (started is null)
            {
                if (mode is "passthrough" or "context_pool") throw new UpstreamException(
                    $"{mode} requires an available upstream Responses WebSocket",
                    ProxyHttpStatus.BadGateway);
                LogTransport(context, mode, "http", null, "context_recovery_failed");
                await foreach (var line in HttpStreamAsync(
                    context,
                    FullReplay(planning.Logical),
                    cancellationToken)) yield return line;
                yield break;
            }

            if (mode == "auto") MarkProbe(probeKey, true);
            LogTransport(
                context,
                mode,
                "websocket",
                planning.Outgoing,
                null);
            Dictionary<string, object?>? terminal = null;
            var sawTerminal = false;
            try
            {
                if (started.First is not null)
                {
                    CaptureTerminal(started.First, ref terminal);
                    sawTerminal = IsTerminal(started.First);
                    if (terminal is not null && !passthrough)
                        Store(owner,contextScope, planning.Logical, terminal);
                    _logger.LogDebug(
                        "Responses upstream event. event_type={EventType} terminal={Terminal} context_scope={ContextScope}",
                        JsonDictionaryValue.String(started.First, "type"),
                        sawTerminal,
                        contextScope);
                    yield return Sse(started.First);
                }

                while (!sawTerminal)
                {
                    var next = await started.Lease.ReceiveAsync(Timeout(context.Route.Channel, context.DefaultTimeout), cancellationToken);
                    if (next is null) break;
                    CaptureTerminal(next, ref terminal);
                    sawTerminal = IsTerminal(next);
                    if (terminal is not null && !passthrough)
                        Store(owner,contextScope, planning.Logical, terminal);
                    _logger.LogDebug(
                        "Responses upstream event. event_type={EventType} terminal={Terminal} context_scope={ContextScope}",
                        JsonDictionaryValue.String(next, "type"),
                        sawTerminal,
                        contextScope);
                    yield return Sse(next);
                }

                _logger.LogDebug(
                    "Responses upstream event loop completed. terminal={Terminal} context_present={ContextPresent} context_scope={ContextScope}",
                    sawTerminal,
                    terminal is not null,
                    contextScope);
                if (sawTerminal && terminal is null && !passthrough)
                    _logger.LogWarning(
                        "Responses upstream stream ended without a reusable context snapshot. context_scope={ContextScope}",
                        contextScope);
                if (!sawTerminal)
                    throw new UpstreamException(
                        "upstream WebSocket closed before a terminal Responses event",
                        ProxyHttpStatus.BadGateway);
            }
            finally
            {
                if (!sawTerminal) started.Lease.Abort();
                await started.DisposeAsync();
                _logger.LogDebug(
                    "Responses upstream stream released. terminal={Terminal} aborted={Aborted} context_scope={ContextScope}",
                    sawTerminal,
                    !sawTerminal,
                    contextScope);
            }
        }
        finally
        {
            conversationLease.Dispose();
        }
    }

    private async Task<StartedStream?> StartFullReplayAsync(
        ProxyStreamContext context,
        IReadOnlyDictionary<string, object?> full,
        string conversation,
        CancellationToken cancellationToken)
    {
        return await TryStartAsync(context, full, conversation, cancellationToken);
    }

    private async Task<StartedStream?> TryStartAsync(
        ProxyStreamContext context,
        IReadOnlyDictionary<string, object?> payload,
        string conversation,
        CancellationToken cancellationToken)
    {
        var fingerprint = ResponsesWebSocketLease.Fingerprint(context.Route.Channel);
        var key = string.Join(':',
            context.ApiKeyId?.ToString("N") ?? context.OwnerUsername,
            conversation,
            context.ChannelId,
            context.UpstreamModel,
            fingerprint);
        ResponsesWebSocketLease? lease = null;
        try
        {
            lease = await sockets.AcquireAsync(
                key,
                context.Route.Channel,
                context.DefaultTimeout,
                cancellationToken);
            await lease.SendAsync(payload, cancellationToken);
            var first = await lease.ReceiveAsync(
                Timeout(context.Route.Channel, context.DefaultTimeout),
                cancellationToken);
            return new StartedStream(lease, first);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (lease is not null)
            {
                lease.Abort();
                await lease.DisposeAsync();
            }
            throw;
        }
        catch (Exception exception)
        {
            if (lease is not null)
            {
                lease.Abort();
                await lease.DisposeAsync();
            }
            _logger.LogWarning(
                exception,
                "Responses upstream WebSocket start failed. channel_id={ChannelId} model={Model} conversation={Conversation}",
                context.ChannelId,
                context.UpstreamModel,
                conversation);
            return null;
        }
    }

    private StreamPlan? PlanContextRequest(string scope, Dictionary<string, object?> request)
    {
        var currentInput = Input(request);
        var previousId = JsonDictionaryValue.String(request, "previous_response_id");
        ContextSnapshot? snapshot = null;
        if (previousId.Length > 0)
        {
            lock (_storeGate)
            { SweepSnapshots();_responses.TryGetValue(scope+":"+previousId,out snapshot);Touch(snapshot); }
            if (snapshot is null)
            {
                LogContextReplay(scope, "previous_response_missing", request, null);
                return null;
            }
        }
        else
        {
            lock (_storeGate)
            { SweepSnapshots();_latest.TryGetValue(scope,out snapshot);Touch(snapshot); }
            if (snapshot is null)
            {
                LogContextReplay(scope, "latest_snapshot_missing", request, null);
                return new StreamPlan(request, request, null);
            }
        }

        var incompatibleProperties = IncompatibleProperties(snapshot.Request, request);
        if (incompatibleProperties.Count > 0)
        {
            LogContextReplay(scope, "properties_changed", request, incompatibleProperties);
            return new StreamPlan(request, request, null);
        }

        var history = JsonDictionaryValue.List(snapshot.Request, "input")
            .Concat(JsonDictionaryValue.List(snapshot.Response, "output"))
            .Select(WebSearchPayload.DeepCopy)
            .ToList();
        var delta = PrefixDelta(history, currentInput);
        if (delta is null)
        {
            LogContextReplay(scope, "input_not_prefixed", request, null);
            return new StreamPlan(request, request, null);
        }

        var logical = WebSearchPayload.DeepCopyObject(request);
        logical["input"] = history.Concat(delta).Select(WebSearchPayload.DeepCopy).ToList();
        logical.Remove("previous_response_id");
        var outgoing = WebSearchPayload.DeepCopyObject(request);
        outgoing["input"] = delta;
        outgoing["previous_response_id"] = JsonDictionaryValue.String(snapshot.Response, "id");
        return new StreamPlan(logical, outgoing, JsonDictionaryValue.String(snapshot.Response, "id"));
    }

    private static List<object?>? PrefixDelta(
        IReadOnlyList<object?> history,
        IReadOnlyList<object?> current)
    {
        if (current.Count < history.Count) return null;
        for (var index = 0; index < history.Count; index++)
        {
            if (!JsonValueComparer.AreEqual(history[index], current[index]))
            {
                return null;
            }
        }
        return current.Skip(history.Count).Select(WebSearchPayload.DeepCopy).ToList();
    }

    private static List<string> IncompatibleProperties(
        IReadOnlyDictionary<string, object?> previous,
        IReadOnlyDictionary<string, object?> current)
    {
        var keys = previous.Keys
            .Concat(current.Keys)
            .Where(key => !ComparisonIgnoredKeys.Contains(key, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var incompatible = new List<string>();
        foreach (var key in keys)
        {
            previous.TryGetValue(key, out var left);
            current.TryGetValue(key, out var right);
            if (!JsonValueComparer.AreEqual(left, right))
                incompatible.Add(key);
        }
        return incompatible;
    }

    private void LogContextReplay(
        string scope,
        string reason,
        IReadOnlyDictionary<string, object?> request,
        IReadOnlyCollection<string>? properties)
    {
        _logger.LogInformation(
            "Responses upstream context did not reuse history. reason={Reason} context_scope={ContextScope} model={Model} input_item_count={InputItemCount} previous_response_id={PreviousResponseId} changed_properties={ChangedProperties}",
            reason,
            scope,
            JsonDictionaryValue.String(request, "model"),
            InputItemCount(request),
            JsonDictionaryValue.String(request, "previous_response_id"),
            properties is null ? null : string.Join(',', properties));
    }

    private void Store(string tenant,string scope,Dictionary<string,object?> request,Dictionary<string,object?> response)
    {
        var id=JsonDictionaryValue.String(response,"id");if(id.Length==0)return;
        var weight=EstimateWeight(request)+EstimateWeight(response)+2L*(scope.Length+id.Length+tenant.Length)+128;
        if(weight>_options.MaxSnapshotBytes || weight>_options.MaxTenantBytes || weight>_options.MaxBytes)
        {
            _logger.LogWarning("Responses context exceeds cache budget; continuation requires full history. context_scope={ContextScope}",scope);
            return;
        }
        var snapshot=new ContextSnapshot { Request=WebSearchPayload.DeepCopyObject(request),Response=WebSearchPayload.DeepCopyObject(response),
            LastUsed=_time.GetUtcNow(),Tenant=tenant,Scope=scope,Weight=weight };
        lock(_storeGate)
        {
            SweepSnapshots();var key=scope+":"+id;
            if(_responses.ContainsKey(key))RemoveSnapshot(key);
            _responses[key]=snapshot;_latest[scope]=snapshot;_snapshotBytes+=weight;
            _tenantBytes[tenant]=_tenantBytes.GetValueOrDefault(tenant)+weight;
            while(_tenantBytes.GetValueOrDefault(tenant)>_options.MaxTenantBytes)
                RemoveSnapshot(_responses.Where(p=>p.Value.Tenant==tenant).MinBy(p=>p.Value.LastUsed).Key);
            while(_snapshotBytes>_options.MaxBytes || _responses.Count>_options.MaxSnapshots)
                RemoveSnapshot(_responses.MinBy(p=>p.Value.LastUsed).Key);
        }
    }
    private void Touch(ContextSnapshot? snapshot) { if(snapshot is not null)snapshot.LastUsed=_time.GetUtcNow(); }
    private void RemoveSnapshot(string key)
    {
        if(!_responses.Remove(key,out var value))return;
        _snapshotBytes-=value.Weight;var remaining=_tenantBytes[value.Tenant]-value.Weight;
        if(remaining==0)_tenantBytes.Remove(value.Tenant);else _tenantBytes[value.Tenant]=remaining;
        if(_latest.TryGetValue(value.Scope,out var latest) && ReferenceEquals(latest,value))_latest.Remove(value.Scope);
    }
    private void SweepSnapshots()
    {
        var expired=_time.GetUtcNow().AddSeconds(-_options.SnapshotTtlSeconds);
        foreach(var key in _responses.Where(p=>p.Value.LastUsed<=expired).Select(p=>p.Key).ToArray())RemoveSnapshot(key);
    }
    internal void CleanupExpired()
    {
        lock(_storeGate)SweepSnapshots();
        lock(_probeGate)
            foreach(var pair in _probes.Where(p=>p.Value.ExpiresAt<=_time.GetUtcNow()).ToArray())_probes.TryRemove(pair.Key,out _);
    }
    // Conservative managed-graph weight: UTF16 content plus container/entry/reference overhead.
    // No full JSON serialization/copy solely to compute cache admission; not an exact heap profiler.
    private long EstimateWeight(object? value,int depth=0)
    {
        var cap=Math.Min(_options.MaxSnapshotBytes,Math.Min(_options.MaxBytes,_options.MaxTenantBytes));
        if(depth>64)return cap+1;
        switch(value)
        {
            case null:return 8;
            case string text:return 24L+2L*text.Length;
            case IReadOnlyDictionary<string,object?> dictionary:
                long dictionaryBytes=128;
                foreach(var pair in dictionary)
                {
                    dictionaryBytes+=64L+2L*pair.Key.Length+EstimateWeight(pair.Value,depth+1);
                    if(dictionaryBytes>cap)return cap+1;
                }
                return dictionaryBytes;
            case IReadOnlyList<object?> items:
                long listBytes=64;
                foreach(var item in items)
                { listBytes+=8L+EstimateWeight(item,depth+1);if(listBytes>cap)return cap+1; }
                return listBytes;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:return 32;
            default:return cap+1; // Unknown/borrowed/mutable objects cannot safely enter a JSON snapshot cache.
        }
    }

    private static Dictionary<string, object?> FullReplay(Dictionary<string, object?> logical)
    {
        var full = WebSearchPayload.DeepCopyObject(logical);
        full.Remove("previous_response_id");
        return full;
    }

    private static Dictionary<string, object?> PassthroughRequest(ProxyStreamContext context)
    {
        var outgoing = WebSearchPayload.DeepCopyObject(context.UpstreamRequest);
        var originalInput = JsonDictionaryValue.Get(context.OriginalPayload, "input");
        if (originalInput is null)
            outgoing.Remove("input");
        else
            outgoing["input"] = WebSearchPayload.DeepCopy(originalInput);

        var previousId = JsonDictionaryValue.String(context.OriginalPayload, "previous_response_id");
        if (previousId.Length == 0)
            outgoing.Remove("previous_response_id");
        else
            outgoing["previous_response_id"] = previousId;
        return outgoing;
    }

    private static bool CanUseWebSocket(ProxyStreamContext context, string mode)
    {
        if (context.ChannelType != ProtocolConverter.Responses
            || context.EntryProtocol != ProtocolConverter.Responses)
            return false;
        if (mode == "off") return false;
        if (mode == "passthrough"
            && !string.Equals(context.RequestMetadata.Method, "GET", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static string ReadMode(IReadOnlyDictionary<string, object?> channel)
    {
        var compat = JsonDictionaryValue.Object(channel, "compat", WebSearchPayload.DeepCopyObject);
        var value = JsonDictionaryValue.String(compat, "ws_mode").Trim().ToLowerInvariant();
        return value is "context_pool" or "passthrough" or "off" ? value : "auto";
    }

    private static string? UnavailableReason(ProxyStreamContext context, string mode)
    {
        if (mode == "off") return "disabled";
        if (context.ChannelType != ProtocolConverter.Responses
            || context.EntryProtocol != ProtocolConverter.Responses)
            return "protocol_mismatch";
        if (mode == "passthrough"
            && !string.Equals(context.RequestMetadata.Method, "GET", StringComparison.OrdinalIgnoreCase))
            return "entry_not_websocket";
        return null;
    }

    private void LogTransport(
        ProxyStreamContext context,
        string requestedMode,
        string transport,
        IReadOnlyDictionary<string, object?>? payload,
        string? fallbackReason)
    {
        var effectiveMode = transport == "websocket"
            ? requestedMode == "passthrough" ? "passthrough" : "context_pool"
            : "off";
        _logger.LogInformation(
            "Responses upstream transport selected. ws_mode_requested={WsModeRequested} effective_mode={EffectiveMode} transport={Transport} channel_id={ChannelId} model={Model} input_item_count={InputItemCount} previous_response_id={PreviousResponseId} fallback_reason={FallbackReason}",
            requestedMode,
            effectiveMode,
            transport,
            context.ChannelId,
            context.UpstreamModel,
            payload is null ? null : InputItemCount(payload),
            payload is null ? null : JsonDictionaryValue.String(payload, "previous_response_id"),
            fallbackReason);
    }

    private static bool IsRecoverableSocketLoss(Dictionary<string, object?> payload)
    {
        var error = JsonDictionaryValue.Object(payload, "error", WebSearchPayload.DeepCopyObject);
        var code = JsonDictionaryValue.String(error, "code");
        return code is "previous_response_not_found" or "websocket_connection_limit_reached";
    }

    private static bool IsTerminal(Dictionary<string, object?> payload)
        => JsonDictionaryValue.String(payload, "type") is
            "response.completed" or "response.failed" or "response.incomplete" or "error";

    private static void CaptureTerminal(
        Dictionary<string, object?> payload,
        ref Dictionary<string, object?>? terminal)
    {
        if (JsonDictionaryValue.String(payload, "type") is "response.completed" or "response.incomplete")
        {
            var response = JsonDictionaryValue.Object(payload, "response", WebSearchPayload.DeepCopyObject);
            if (response.Count > 0) terminal = response;
        }
    }

    private static SseFrame Sse(Dictionary<string, object?> payload)
    {
        var type = JsonDictionaryValue.String(payload, "type");
        return new SseFrame($"event: {type}\ndata: {JsonSerializer.Serialize(payload)}\n\n", new SseEvent(type, payload));
    }

    private async IAsyncEnumerable<SseFrame> HttpStreamAsync(
        ProxyStreamContext context,
        IReadOnlyDictionary<string, object?> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in http.StreamEventsAsync(
            context.Route.Channel,
            payload,
            context.DefaultTimeout,
            cancellationToken))
        {
            yield return line;
        }
    }

    private static List<object?> Input(IReadOnlyDictionary<string, object?> request)
    {
        var input = JsonDictionaryValue.Get(request, "input");
        if (input is string text)
        {
            return
            [
                new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = text
                }
            ];
        }
        return JsonDictionaryValue.List(request, "input").Select(WebSearchPayload.DeepCopy).ToList();
    }

    private static int InputItemCount(IReadOnlyDictionary<string, object?> request)
    {
        var input = JsonDictionaryValue.Get(request, "input");
        return input is string ? 1 : JsonDictionaryValue.List(request, "input").Count;
    }

    private static int Timeout(IReadOnlyDictionary<string, object?> channel, int fallback)
    {
        var value = JsonDictionaryValue.Get(channel, "timeout_seconds");
        return value switch
        {
            int number when number > 0 => number,
            long number when number > 0 && number <= int.MaxValue => (int)number,
            double number when number > 0 && number <= int.MaxValue => (int)number,
            string text when int.TryParse(text, out var parsed) && parsed > 0 => parsed,
            _ => fallback
        };
    }

    private void MarkProbe(string key,bool supported)
    {
        lock(_probeGate)
        {
            foreach(var pair in _probes.Where(p=>p.Value.ExpiresAt<=_time.GetUtcNow()).ToArray())_probes.TryRemove(pair.Key,out _);
            _probes[key]=new ProbeState { Supported=supported,ExpiresAt=_time.GetUtcNow().AddSeconds(_options.ProbeTtlSeconds) };
            while(_probes.Count>_options.MaxProbes) _probes.TryRemove(_probes.MinBy(p=>p.Value.ExpiresAt).Key,out _);
        }
    }

    private sealed class ContextSnapshot
    {
        public Dictionary<string, object?> Request { get; init; } = [];
        public Dictionary<string, object?> Response { get; init; } = [];
        public DateTimeOffset LastUsed { get; set; }
        public string Tenant { get; init; }="";
        public string Scope { get; init; }="";
        public long Weight { get; init; }
    }

    private sealed class StreamPlan(
        Dictionary<string, object?> logical,
        Dictionary<string, object?> outgoing,
        string? previousId)
    {
        public Dictionary<string, object?> Logical { get; } = logical;
        public Dictionary<string, object?> Outgoing { get; } = outgoing;
        public string? PreviousId { get; } = previousId;
    }

    private sealed class ProbeState
    {
        public bool Supported { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private sealed class StartedStream(ResponsesWebSocketLease lease, Dictionary<string, object?>? first)
        : IAsyncDisposable
    {
        public ResponsesWebSocketLease Lease { get; } = lease;
        public Dictionary<string, object?>? First { get; } = first;
        public ValueTask DisposeAsync() => Lease.DisposeAsync();
    }
}

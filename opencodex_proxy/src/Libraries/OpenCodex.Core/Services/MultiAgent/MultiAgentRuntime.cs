using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.MultiAgent;

// One coordinator owns state transitions; model calls run concurrently in separate DI scopes.
public sealed partial class MultiAgentRuntime
{
    private readonly MultiAgentRun _run;
    private readonly MultiAgentModelCall _model;
    private readonly Func<Dictionary<string, object?>, Task> _emit;
    private readonly Func<Task> _save;
    private readonly TimeProvider _time;
    private readonly MultiAgentClientRuntimeHooks? _client;
    private readonly Dictionary<string, (Task<Dictionary<string, object?>> Task, CancellationTokenSource Stop, int Generation, string Round)> _active = [];
    private readonly List<object?> _output = [];
    private int _sequence;

    public MultiAgentRuntime(MultiAgentRun run,
        Func<Dictionary<string, object?>, CancellationToken, Task<Dictionary<string, object?>>> model,
        Func<Dictionary<string, object?>, Task> emit, Func<Task> save, TimeProvider? timeProvider = null,
        MultiAgentClientRuntimeHooks? client = null)
        : this(run, (payload, _, token) => model(payload, token), emit, save, timeProvider, client) { }

    public MultiAgentRuntime(MultiAgentRun run, MultiAgentModelCall model,
        Func<Dictionary<string, object?>, Task> emit, Func<Task> save, TimeProvider? timeProvider = null,
        MultiAgentClientRuntimeHooks? client = null)
    {
        _run = run;
        _model = model;
        _emit = emit;
        _save = save;
        _time = timeProvider ?? TimeProvider.System;
        _client = client;
    }

    public async Task<Dictionary<string, object?>> ExecuteAsync(Dictionary<string, object?> request, CancellationToken ct,
        ChannelReader<MultiAgentInjection>? injections = null)
    {
        _run.Template = MultiAgentProtocol.ApplyUpdates(_run.Template, request);
        if (_client is not null) _run.Template = _client.Tools.ApplyToTemplate(_run.Template);
        var instructions = JsonDictionaryValue.List(request, "input").OfType<Dictionary<string, object?>>()
            .Where(i => Text(i, "role") is "system" or "developer" && Text(i, "type") is "" or "message").ToList();
        if (instructions.Count > 0) _run.InstructionMessages = instructions.Select(WebSearchPayload.DeepCopy).ToList();
        await AcceptInput(request, ct);
        _run.LastResponseId = Id("resp");
        await _save();
        await Event("response.created", ("response", Response("in_progress")));
        await Event("response.in_progress", ("response", Response("in_progress")));
        var knownCalls = JsonDictionaryValue.List(request, "input").OfType<Dictionary<string, object?>>()
            .Where(i => Text(i, "type") is "function_call" or "custom_tool_call").Select(i => Text(i, "call_id")).ToHashSet();
        foreach (var pending in _run.PendingCalls.Where(p => !knownCalls.Contains(p.Key)))
        {
            var call = _run.Agents[pending.Value].History.OfType<Dictionary<string, object?>>()
                .First(i => Text(i, "call_id") == pending.Key);
            await Item(pending.Value, call);
        }
        var startInput = _run.InputTokens;
        var startOutput = _run.OutputTokens;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                while (_modelEvents.Reader.TryRead(out var modelEvent)) await ProcessModelEvent(modelEvent);
                if (injections is not null)
                {
                    while (injections.TryRead(out var injection)) await Inject(injection, ct);
                }
                foreach (var agent in _run.Agents.Values.ToList())
                {
                    if (agent.WaitingCallId is not null && (agent.Mailbox.Count > 0 || agent.WaitUntil <= _time.GetUtcNow()))
                        await CompleteWait(agent);
                }

                foreach (var agent in _run.Agents.Values.Where(a => a.Status == "ready").ToList())
                {
                    if (_active.ContainsKey(agent.Name) || agent.WaitingCallId is not null || _run.PendingCalls.Values.Contains(agent.Name)) continue;
                    if (agent.Name != "/root" && _active.Keys.Count(n => n != "/root") >= _run.MaxConcurrentSubagents)
                        continue;
                    var compact = agent.LastInputTokens >= _run.CompactThresholdTokens;
                    var calls = compact ? 2 : 1;
                    BeginTask(agent);
                    DeliverMailbox(agent);
                    var payload = WebSearchPayload.DeepCopyObject(_run.Template);
                    var history = agent.History.Select(WebSearchPayload.DeepCopy).ToList();
                    if (_run.InstructionMessages is not null)
                        history = _run.InstructionMessages.Select(WebSearchPayload.DeepCopy).Concat(history.Where(i =>
                            i is not Dictionary<string, object?> d || Text(d, "role") is not ("system" or "developer"))).ToList();
                    history.Add(Message("developer", AgentInstructions(agent)));
                    payload["input"] = history;
                    payload["stream"] = false;
                    payload["prompt_cache_key"] = $"{_run.Id}:{_client?.AgentName ?? agent.Name}";
                    agent.Status = "running";
                    _run.ModelTurns += calls;
                    var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var round = Id("round");
                    _active.Add(agent.Name, (RunModel(payload, compact,
                        (e, _) => EnqueueModelEvent(agent.Name, round, e, stop.Token), stop.Token), stop, agent.CurrentTaskGeneration, round));
                }

                if (_active.Count == 0)
                {
                    // HTTP hands control back once only client tools (or completed agents) remain.
                    if (_run.PendingCalls.Count > 0)
                    {
                        if (injections is null) break;
                        var deadline = _run.Agents.Values.Where(a => a.WaitingCallId is not null)
                            .Select(a => a.WaitUntil!.Value).Order().FirstOrDefault();
                        if (deadline == default)
                        {
                            if (!await injections.WaitToReadAsync(ct)) throw new OperationCanceledException(ct);
                        }
                        else
                        {
                            var timer = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, (deadline - _time.GetUtcNow()).TotalMilliseconds)), _time, ct);
                            await Task.WhenAny(timer, injections.WaitToReadAsync(ct).AsTask());
                        }
                        continue;
                    }
                    var waiting = _run.Agents.Values.Where(a => a.Status == "waiting").ToList();
                    if (waiting.Count > 0)
                    {
                        var delay = waiting.Min(a => a.WaitUntil!.Value) - _time.GetUtcNow();
                        if (delay > TimeSpan.Zero)
                        {
                            var timer = Task.Delay(delay, _time, ct);
                            if (injections is null) await timer;
                            else await Task.WhenAny(timer, injections.WaitToReadAsync(ct).AsTask());
                        }
                        continue;
                    }
                    _run.Finished = _run.Agents["/root"].Status == "completed"
                        && !HasUnfinishedDescendants(_run.Agents["/root"])
                        && _run.Agents["/root"].Mailbox.Count == 0;
                    break;
                }

                var waits = _active.Values.Select(v => (Task)v.Task).ToList();
                waits.Add(_modelEvents.Reader.WaitToReadAsync(ct).AsTask());
                if (injections is not null) waits.Add(injections.WaitToReadAsync(ct).AsTask());
                var deadlines = _run.Agents.Values.Where(a => a.WaitingCallId is not null).Select(a => a.WaitUntil!.Value).ToList();
                if (deadlines.Count > 0)
                    waits.Add(Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, (deadlines.Min() - _time.GetUtcNow()).TotalMilliseconds)), _time, ct));
                var finished = await Task.WhenAny(waits);
                if (!_active.Values.Any(v => v.Task == finished)) continue;
                var name = _active.First(p => p.Value.Task == finished).Key;
                var operation = _active[name];
                _active.Remove(name);
                var current = _run.Agents[name];
                try
                {
                    var result = await operation.Task;
                    if (!operation.Stop.IsCancellationRequested)
                    {
                        await ProcessTurn(current, result, operation.Round, ct);
                    }
                    else await CloseModelItems(operation.Round);
                }
                catch (OperationCanceledException) when (operation.Stop.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    await CloseModelItems(operation.Round);
                }
                catch (IncompleteModelResponse exception) when (current.Parent.Length > 0)
                {
                    await CloseModelItems(operation.Round);
                    await FailAgent(current, "incomplete", JsonSerializer.Serialize(exception.Details));
                }
                catch (ProxyException exception) when (current.Parent.Length > 0)
                {
                    await CloseModelItems(operation.Round);
                    await FailAgent(current, "failed", exception.Message);
                }
                finally { operation.Stop.Dispose(); }
                await _save();
            }
            var response = Response("completed");
            response["usage"] = Usage(_run.InputTokens - startInput, _run.OutputTokens - startOutput);
            await _save();
            await Event("response.completed", ("response", response));
            return response;
        }
        catch (IncompleteModelResponse exception)
        {
            var root = _run.Agents["/root"];
            root.Status = "incomplete";
            root.LastError = JsonSerializer.Serialize(exception.Details);
            FinishTask(root, "incomplete");
            await CloseModelItems();
            var response = Response("incomplete");
            response["incomplete_details"] = exception.Details;
            response["usage"] = Usage(_run.InputTokens - startInput, _run.OutputTokens - startOutput);
            await Event("response.incomplete", ("response", response));
            return response;
        }
        catch (ProxyException exception)
        {
            var root = _run.Agents["/root"];
            root.Status = "failed";
            root.LastError = exception.Message;
            FinishTask(root, "failed");
            await CloseModelItems();
            var response = Response("failed");
            response["error"] = new Dictionary<string, object?> { ["code"] = exception.ErrorType, ["message"] = exception.Message };
            response["usage"] = Usage(_run.InputTokens - startInput, _run.OutputTokens - startOutput);
            await Event("response.failed", ("response", response));
            return response;
        }
        finally
        {
            foreach (var operation in _active.Values) operation.Stop.Cancel();
            while (_modelEvents.Reader.TryRead(out var pendingEvent)) pendingEvent.Accepted.TrySetCanceled();
            foreach (var operation in _active.Values)
            {
                try { await operation.Task; } catch (OperationCanceledException) { } catch (ProxyException) { }
                operation.Stop.Dispose();
            }
            foreach (var agent in _run.Agents.Values.Where(a => a.Status == "running")) agent.Status = "ready";
            _active.Clear();
            await _save();
        }
    }

    private async Task Inject(MultiAgentInjection injection, CancellationToken ct)
    {
        var error = injection.ResponseId == _run.LastResponseId ? null : "response_not_found";
        if (error is null && injection.Input.OfType<Dictionary<string, object?>>()
            .Any(i => !_run.PendingCalls.ContainsKey(Text(i, "call_id")) && !_run.ReceivedCalls.Contains(Text(i, "call_id"))))
            error = "invalid_tool_call";
        if (error is not null)
        {
            await Event("response.inject.failed", ("response_id", injection.ResponseId), ("input", injection.Input),
                ("error", new { code = error, message = "The injection does not reference an active response and pending tool call." }));
            return;
        }
        await AcceptInput(new Dictionary<string, object?> { ["input"] = injection.Input }, ct);
        await _save();
        await Event("response.inject.created", ("response_id", injection.ResponseId));
    }

    private async Task AcceptInput(Dictionary<string, object?> request, CancellationToken ct)
    {
        var input = JsonDictionaryValue.Get(request, "input") is string text
            ? new List<object?> { Message("user", text) } : JsonDictionaryValue.List(request, "input");
        if (_client is not null && _run.ModelTurns == 0 && _run.PendingCalls.Count == 0)
        {
            // The server has already seeded the fork. Client-inherited results are historical,
            // not results of new calls owned by this actor; remember them for full-history retries.
            foreach (var inherited in input.OfType<Dictionary<string, object?>>()
                         .Where(item => Text(item, "type") is "function_call_output" or "custom_tool_call_output"))
            {
                var inheritedId = Text(inherited, "call_id");
                if (inheritedId.Length > 0) _run.ReceivedCalls.Add(inheritedId);
            }
        }
        foreach (var item in input.OfType<Dictionary<string, object?>>())
        {
            var type = Text(item, "type");
            if (type is not ("function_call_output" or "custom_tool_call_output")) continue;
            var callId = Text(item, "call_id");
            if (_run.ReceivedCalls.Contains(callId)) continue;
            if (!_run.PendingCalls.TryGetValue(callId, out var agentName))
            {
                if (callId.StartsWith("call_ma_", StringComparison.Ordinal))
                    throw new BadRequestException($"Unknown pending multi-agent tool call: {callId}");
                continue;
            }
            var agent = _run.Agents[agentName];
            var clean = WebSearchPayload.DeepCopyObject(item);
            clean.Remove("agent");
            if (_client?.ToolResult is { } receive)
            {
                var call = agent.History.OfType<Dictionary<string, object?>>()
                    .FirstOrDefault(i => Text(i, "call_id") == callId && Text(i, "type") is "function_call" or "custom_tool_call")
                    ?? throw new UpstreamException("Pending client tool call is missing from agent history.");
                await receive(WebSearchPayload.DeepCopyObject(call), WebSearchPayload.DeepCopyObject(clean), ct);
            }
            _run.PendingCalls.Remove(callId);
            var completedTurn = agent.CompletedTurns.FirstOrDefault(t => t.Items.OfType<Dictionary<string, object?>>()
                .Any(i => Text(i, "call_id") == callId && Text(i, "type") is "function_call" or "custom_tool_call"));
            if (completedTurn is null) agent.History.Add(clean);
            else
            {
                completedTurn.Items.Add(WebSearchPayload.DeepCopy(clean));
                var insertion = agent.History.FindIndex(i => i is Dictionary<string, object?> d && Text(d, "call_id") == callId) + 1;
                while (insertion < agent.History.Count && agent.History[insertion] is Dictionary<string, object?> d
                    && Text(d, "type") is "function_call" or "custom_tool_call" or "function_call_output" or "custom_tool_call_output") insertion++;
                agent.History.Insert(insertion, clean);
                if (insertion <= agent.CurrentTurnStart) agent.CurrentTurnStart++;
            }
            _run.ReceivedCalls.Add(callId);
            if (agent.Status == "tool_wait" && !_run.PendingCalls.Values.Contains(agentName)) agent.Status = "ready";
        }
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in input.OfType<Dictionary<string, object?>>()
            .Where(i => Text(i, "role") == "user" && Text(i, "type") is "" or "message"))
        {
            var key = Text(item, "id");
            if (key.Length == 0)
            {
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item))));
                var occurrence = occurrences.GetValueOrDefault(contentHash) + 1;
                occurrences[contentHash] = occurrence;
                key = $"{Text(request, "previous_response_id")}:{contentHash}:{occurrence}";
            }
            if (_run.SeenUserMessages.Add(key) && _run.ModelTurns > 0)
            {
                var root = _run.Agents["/root"];
                QueueTask(root, [WebSearchPayload.DeepCopy(item)], JsonSerializer.Serialize(item));
                _run.Finished = false;
            }
        }
        if (_client is not null)
        {
            var reports = new List<object?>();
            foreach (var message in input.OfType<Dictionary<string, object?>>().Where(item => Text(item, "type") == "agent_message"))
            {
                var converted = (Dictionary<string, object?>)MultiAgentProtocol.InitialHistory(new()
                    { ["input"] = new List<object?> { message } }).Single()!;
                var id = Text(message, "id");
                if (id.Length > 0) converted["id"] = message["id"];
                var key = id.Length > 0 ? "client-agent-message:id:" + id
                    : "client-agent-message:content:" + Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(converted))));
                if (_run.SeenUserMessages.Add(key)) reports.Add(converted);
            }
            if (reports.Count > 0 && _run.ModelTurns > 0)
            {
                var root = _run.Agents["/root"];
                // Reports belong to the currently running task; a completed actor resumes once for the batch.
                if (root.TaskStarted) root.Mailbox.AddRange(reports);
                else QueueTask(root, reports, JsonSerializer.Serialize(reports));
                _run.Finished = false;
            }
        }
        _run.ClientInputCount = input.Count;
    }

    private async Task ProcessTurn(MultiAgentState agent, Dictionary<string, object?> response, string round, CancellationToken ct)
    {
        var usage = JsonDictionaryValue.Object(response, "usage", WebSearchPayload.DeepCopyObject);
        agent.LastInputTokens = WebSearchPayload.ToInt(JsonDictionaryValue.Get(usage, "input_tokens"), 0);
        if (response.Remove("_ocxp_compacted_history", out var compacted)) ApplyCompactedHistory(agent, (List<object?>)compacted!);
        _run.InputTokens += WebSearchPayload.ToInt(JsonDictionaryValue.Get(response, "_ocxp_summary_input_tokens"), 0);
        _run.OutputTokens += WebSearchPayload.ToInt(JsonDictionaryValue.Get(response, "_ocxp_summary_output_tokens"), 0);
        _run.InputTokens += WebSearchPayload.ToInt(JsonDictionaryValue.Get(usage, "input_tokens"), 0);
        _run.OutputTokens += WebSearchPayload.ToInt(JsonDictionaryValue.Get(usage, "output_tokens"), 0);
        if (Text(response, "status") == "incomplete")
            throw new IncompleteModelResponse(JsonDictionaryValue.Get(response, "incomplete_details"));
        if (Text(response, "status") == "failed")
            throw new BadRequestException("A model response failed; the multi-agent task has not completed.");
        var items = JsonDictionaryValue.List(response, "output").OfType<Dictionary<string, object?>>().ToList();
        var forkHistory = agent.History.Select(WebSearchPayload.DeepCopy).ToList();
        var hasCalls = items.Any(i => Text(i, "type") is "function_call" or "custom_tool_call");
        if (!hasCalls && !items.Any(i => Text(i, "type") == "message"))
            throw new UpstreamException("The model returned neither a message nor a supported tool call.");
        agent.Status = hasCalls ? "ready"
            : agent.Mailbox.Count > 0 ? "ready"
            : HasUnfinishedDescendants(agent) ? "finalizing" : "completed";
        var preparedCalls = new List<Dictionary<string, object?>>();
        try
        {
            if (_client is not null)
            {
                // Validate the complete batch before creating any external reservations.
                foreach (var item in items.Where(item => Text(item, "type") is "function_call" or "custom_tool_call"))
                {
                    if (MultiAgentProtocol.ActionName(Text(item, "name")) is not null)
                        throw new UpstreamException("The model returned a server-only collaboration action in native client mode.");
                    if (_client.Tools.Action(item) is not null) _client.Tools.Arguments(item);
                }
            }
            for (var sourceIndex = 0; sourceIndex < items.Count; sourceIndex++)
            {
                var raw = items[sourceIndex];
                var item = WebSearchPayload.DeepCopyObject(raw);
                item.Remove("agent");
                var type = Text(item, "type");
                if (type is "function_call" or "custom_tool_call")
                {
                    item["call_id"] = FindLiveItem(round, sourceIndex)?.CallId ?? Id("call_ma");
                    if (_client?.BeforeToolCall is { } prepare)
                    {
                        await prepare(agent, item, forkHistory.Select(WebSearchPayload.DeepCopy).ToList(), ct);
                        preparedCalls.Add(WebSearchPayload.DeepCopyObject(item));
                    }
                }
                agent.History.Add(item);
            }
        }
        catch
        {
            agent.History.RemoveRange(forkHistory.Count, agent.History.Count - forkHistory.Count);
            // Cleanup must finish even when cancellation caused preparation to fail.
            if (preparedCalls.Count > 0 && _client?.RollbackToolCalls is { } rollback)
                await rollback(preparedCalls, CancellationToken.None);
            throw;
        }
        var terminalItems = agent.History.Skip(forkHistory.Count).OfType<Dictionary<string, object?>>().ToList();
        for (var sourceIndex = 0; sourceIndex < terminalItems.Count; sourceIndex++)
        {
            var item = terminalItems[sourceIndex];
            var type = Text(item, "type");
            if (_client is null && type == "function_call" && MultiAgentProtocol.ActionName(Text(item, "name")) is { } action)
                await ExecuteAction(agent, item, action, forkHistory);
            else if (type is "function_call" or "custom_tool_call")
            {
                _run.PendingCalls.Add(Text(item, "call_id"), agent.Name);
                agent.Status = "tool_wait";
                await _save();
                await FinishModelItem(agent.Name, round, sourceIndex, item);
            }
            else if (type == "message")
            {
                item["phase"] = agent.Status == "completed" ? "final_answer" : "commentary";
                await FinishModelItem(agent.Name, round, sourceIndex, item);
            }
            else if (FindLiveItem(round, sourceIndex) is not null)
                await FinishModelItem(agent.Name, round, sourceIndex, item);
        }
        if (!hasCalls && agent.Status == "completed") FinishTask(agent, "completed");
        if (_client is null && !hasCalls && agent.Status == "completed" && agent.Parent.Length > 0)
        {
            var text = string.Join("\n", items.Where(i => Text(i, "type") == "message")
                .SelectMany(i => JsonDictionaryValue.List(i, "content")).OfType<Dictionary<string, object?>>()
                .Select(i => Text(i, "text")));
            await Send(agent.Name, agent.Parent, text, "FINAL_ANSWER");
        }
        if (!hasCalls && agent.Status == "completed") StartNextTask(agent);
    }

    private void DeliverMailbox(MultiAgentState agent)
    {
        agent.History.AddRange(agent.Mailbox);
        agent.Mailbox.Clear();
    }

    private string AgentInstructions(MultiAgentState agent)
    {
        if (_client is not null)
            return $"Your identity is {_client.AgentName}. Parent: {_client.ParentName}. Use the provided collaboration tools for agent coordination. The client manages native agent threads, tools and lifecycle; the OpenCodex server manages each agent's model context and token-based compaction. All agents share the client workspace. Your final answer completes your current task."
                + (_client.ParentName.Length > 0
                    ? " Inherited conversation is background context. Execute your latest assigned task; the ancestor's request to delegate has already been fulfilled by creating you."
                    : " Synthesize completed subagent reports before giving your final answer.");
        return $"{MultiAgentProtocol.RootInstructions}\nYour identity is {agent.Name}. Parent: {agent.Parent}. Use only ocxp_ma_* actions for agent coordination. Your final answer completes your current task and is delivered to your parent. All agents share the client workspace.\n{(agent.Parent.Length > 0 ? $"Inherited conversation is background context. Execute your latest assigned task below; the ancestor's request to delegate has already been fulfilled by creating you.\nCurrent assignment: {agent.LastTaskMessage}" : "Synthesize completed subagent reports before giving your final answer.")}";
    }

    private async Task Item(string agent, Dictionary<string, object?> source)
    {
        var item = WebSearchPayload.DeepCopyObject(source);
        item["id"] = Id(Text(item, "type") == "message" ? "msg" : "item");
        item["agent"] = new Dictionary<string, object?> { ["agent_name"] = _client?.AgentName ?? agent };
        var index = _output.Count;
        _output.Add(item);
        _run.OutputHistory.Add(item);
        var added = WebSearchPayload.DeepCopyObject(item);
        var type = Text(item, "type");
        if (type == "message") { added["content"] = new List<object?>(); added["status"] = "in_progress"; }
        if (type == "function_call") added["arguments"] = "";
        if (type == "custom_tool_call") added["input"] = "";
        await Event("response.output_item.added", ("output_index", index), ("item", added), ("agent", item["agent"]));
        if (type == "message")
        {
            var content = JsonDictionaryValue.List(item, "content");
            for (var n = 0; n < content.Count; n++)
            {
                var part = (Dictionary<string, object?>)content[n]!;
                await Event("response.content_part.added", ("item_id", item["id"]), ("output_index", index), ("content_index", n), ("part", new Dictionary<string, object?> { ["type"] = "output_text", ["text"] = "", ["annotations"] = new List<object?>() }), ("agent", item["agent"]));
                await Event("response.output_text.delta", ("item_id", item["id"]), ("output_index", index), ("content_index", n), ("delta", Text(part, "text")), ("agent", item["agent"]));
                await Event("response.output_text.done", ("item_id", item["id"]), ("output_index", index), ("content_index", n), ("text", Text(part, "text")), ("agent", item["agent"]));
                await Event("response.content_part.done", ("item_id", item["id"]), ("output_index", index), ("content_index", n), ("part", part), ("agent", item["agent"]));
            }
        }
        else if (type is "function_call" or "custom_tool_call")
        {
            var field = type == "function_call" ? "arguments" : "input";
            var input = JsonDictionaryValue.Get(item, field)?.ToString() ?? "";
            await Event($"response.{type}_{field}.delta", ("item_id", item["id"]), ("output_index", index), ("delta", input), ("agent", item["agent"]));
            await Event($"response.{type}_{field}.done", ("item_id", item["id"]), ("output_index", index), (field, input), ("agent", item["agent"]));
        }
        await Event("response.output_item.done", ("output_index", index), ("item", item), ("agent", item["agent"]));
    }

    private Task Event(string type, params (string Key, object? Value)[] fields)
    {
        var data = fields.ToDictionary(p => p.Key, p => p.Value);
        data["type"] = type;
        data["sequence_number"] = _sequence++;
        return _emit(data);
    }

    private Dictionary<string, object?> Response(string status) => new()
    {
        ["id"] = _run.LastResponseId, ["object"] = "response", ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["status"] = status, ["model"] = _run.Model, ["output"] = _output.ToList(), ["error"] = null,
        ["parallel_tool_calls"] = true, ["usage"] = Usage(0, 0)
    };
    private static Dictionary<string, object?> Usage(long input, long output) => new()
    { ["input_tokens"] = input, ["output_tokens"] = output, ["total_tokens"] = input + output };
    private static string Id(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    private static string Text(IReadOnlyDictionary<string, object?> value, string key) => JsonDictionaryValue.String(value, key);
    private static Dictionary<string, object?> Message(string role, string text) => new()
    { ["type"] = "message", ["role"] = role, ["content"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = text } } };

    private sealed class IncompleteModelResponse(object? details) : Exception
    {
        public object? Details { get; } = details;
    }
}

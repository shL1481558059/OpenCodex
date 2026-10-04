using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Api.Services;

public sealed partial class MultiAgentResponseService
{
    public async Task<IActionResult?> TryClientResponsesAsync(Dictionary<string, object?> request, bool allowCreate = false)
    {
        if (JsonDictionaryValue.Get(JsonDictionaryValue.Object(request, "multi_agent", WebSearchPayload.DeepCopyObject), "enabled") is false)
            return null;
        var metadata = ProxyRequestMetadataFactory.FromHttpRequest(Request, HttpContext.Connection.RemoteIpAddress?.ToString());
        var identity = MultiAgentClientIdentity.Parse(metadata.Headers);
        var binding = await ResolveClientBinding(request, identity, allowCreate, HttpContext.RequestAborted);
        if (binding is null) return null;

        Response.Headers["X-OpenCodex-Multi-Agent-Session"] = binding.RootThreadId;
        var stream = JsonDictionaryValue.Get(request, "stream") is true;
        async Task Emit(Dictionary<string, object?> item)
        {
            if (!stream) return;
            if (!Response.HasStarted) ProxyStreamResponseWriter.PrepareSse(Response);
            await Response.WriteAsync($"event: {JsonDictionaryValue.String(item, "type")}\ndata: {JsonSerializer.Serialize(item)}\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);
        }

        var result = await ExecuteClient(request, binding, metadata, Emit, HttpContext.RequestAborted);
        return stream ? new EmptyResult() : new ObjectResult(result) { StatusCode = StatusCodes.Status200OK };
    }

    private async Task<MultiAgentClientBinding?> ResolveClientBinding(Dictionary<string, object?> request,
        MultiAgentClientIdentity? identity, bool allowCreate, CancellationToken ct)
    {
        if (identity is null)
        {
            if (allowCreate && MultiAgentClientTools.FromRequest(request).Definitions.Count > 0)
                throw new BadRequestException("Native collaboration requires a client thread identity. Supply thread-id or x-codex-turn-metadata.thread_id; server-only legacy agents cannot create native child chats.");
            return null;
        }
        var owner = identityContext.RequireIdentity().ApiKeyId;
        var model = JsonDictionaryValue.String(request, "model");
        if (model.Length == 0) throw new BadRequestException("model is required.");
        var tools = MultiAgentClientTools.FromRequest(request);
        try
        {
            var binding = await clientStore.TryGetAsync(owner, identity.RootThreadId, identity.ThreadId, ct);
            if (identity.ParentThreadId.Length > 0)
            {
                var root = await clientStore.TryGetAsync(owner, identity.RootThreadId, identity.RootThreadId, ct);
                if (root is null)
                {
                    if (allowCreate) throw new BadRequestException("The parent client-agent session is unavailable. Start a new root task.");
                    return null;
                }
                binding = await clientStore.AttachAsync(owner, identity.RootThreadId, identity.ParentThreadId,
                    identity.ThreadId, identity.AgentName, model, ct);
            }
            else if (binding is null)
            {
                if (!allowCreate) return null;
                if (!tools.CanSpawn)
                {
                    if (tools.Definitions.Count > 0)
                        throw new BadRequestException("Starting a native collaboration session requires the client's spawn_agent declaration. Server-only legacy agents cannot replace native collaboration tools.");
                    return null;
                }
                if (JsonDictionaryValue.String(request, "previous_response_id").Length > 0)
                    throw new BadRequestException("The previous client-agent session is unavailable. Start a new root task.");
                binding = await clientStore.OpenRootAsync(owner, identity.ThreadId,
                    () => CreateClientRun(request, tools), JsonDictionaryValue.Get(request, "store") is not false, ct);
            }

            if (binding is null) return null;
            if (binding.Run.Model != model)
                throw new BadRequestException("The request model must match the client agent's reserved model.");
            if (binding.Persist && JsonDictionaryValue.Get(request, "store") is false)
                throw new BadRequestException("Client agents must use the root session's storage policy. Start a root task with store:false for memory-only execution.");
            return binding;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new BadRequestException(error.Message);
        }
    }

    private MultiAgentRun CreateClientRun(Dictionary<string, object?> request, MultiAgentClientTools tools) => new()
    {
        Model = JsonDictionaryValue.String(request, "model"),
        Template = tools.ApplyToTemplate(MultiAgentProtocol.NormalizeRequest(request)),
        ClientTools = tools.Definitions.Select(WebSearchPayload.DeepCopy).ToList(),
        MaxConcurrentSubagents = ConcurrentLimit(JsonDictionaryValue.Object(request, "multi_agent", WebSearchPayload.DeepCopyObject)),
        CompactThresholdTokens = CompactThreshold(request),
        Agents = new(StringComparer.Ordinal)
        {
            ["/root"] = new() { Name = "/root", History = MultiAgentProtocol.InitialHistory(request) }
        }
    };

    private async Task<Dictionary<string, object?>> ExecuteClient(Dictionary<string, object?> request,
        MultiAgentClientBinding binding, ProxyRequestMetadata metadata,
        Func<Dictionary<string, object?>, Task> emit, CancellationToken ct,
        ChannelReader<MultiAgentInjection>? injections = null)
    {
        var gate = clientStore.Gate(binding);
        await gate.WaitAsync(ct);
        try
        {
            var previous = JsonDictionaryValue.String(request, "previous_response_id");
            if (previous.Length > 0 && !binding.Run.ResponseIds.Contains(previous) && binding.Run.LastResponseId != previous)
                throw new BadRequestException("The previous response does not belong to this client agent.");
            var tools = request.ContainsKey("tools")
                ? MultiAgentClientTools.FromRequest(request)
                : MultiAgentClientTools.FromDefinitions(binding.Run.ClientTools.Concat(MultiAgentClientTools.FromRequest(request).Definitions));
            binding.Run.ClientTools = tools.Definitions.Select(WebSearchPayload.DeepCopy).ToList();
            using var activity = clientStore.BeginActivity(binding, ct);
            var hooks = new MultiAgentClientRuntimeHooks
            {
                AgentName = binding.AgentName, ParentName = binding.ParentName, Tools = tools,
                BeforeToolCall = (caller, call, history, token) => PrepareClientCall(binding, tools, caller, call, history, token),
                ToolResult = (call, output, token) => AcceptClientControlResult(binding, call, output, token),
                RollbackToolCalls = (calls, token) => RollbackClientCalls(binding, calls, token)
            };
            async Task<Dictionary<string, object?>> Model(Dictionary<string, object?> payload,
                Func<Dictionary<string, object?>, CancellationToken, Task> onEvent, CancellationToken token)
            {
                await using var permit = await clientStore.AcquireModelAsync(binding, token);
                return await CallModelAsync(payload, metadata, onEvent, token);
            }
            var runtime = new MultiAgentRuntime(binding.Run, Model, emit,
                () => clientStore.SaveAsync(binding, CancellationToken.None), client: hooks);
            return await runtime.ExecuteAsync(request, activity.Token, injections);
        }
        finally { gate.Release(); }
    }

    private async Task PrepareClientCall(MultiAgentClientBinding binding, MultiAgentClientTools tools,
        MultiAgentState caller, Dictionary<string, object?> call, List<object?> history, CancellationToken ct)
    {
        var action = tools.Action(call);
        if (action is null) return;
        var args = tools.Arguments(call);
        var callId = JsonDictionaryValue.String(call, "call_id");
        var control = new Dictionary<string, object?> { ["action"] = action, ["arguments"] = args };
        if (action != "spawn_agent")
        {
            if (action == "interrupt_agent")
            {
                var target = await clientStore.TryGetAgentAsync(binding, JsonDictionaryValue.String(args, "target"), ct);
                if (target is not null)
                {
                    control["target_id"] = target.Id;
                    control["activity_id"] = clientStore.ActiveRequestId(target);
                }
            }
            binding.Run.ClientControlCalls[callId] = control;
            return;
        }

        var taskName = JsonDictionaryValue.String(args, "task_name");
        var message = JsonDictionaryValue.Get(args, "message") as string
            ?? throw new BadRequestException("spawn_agent requires a string message.");
        var model = JsonDictionaryValue.String(args, "model");
        if (model.Length == 0) model = binding.Run.Model;
        var fork = JsonDictionaryValue.String(args, "fork_turns");
        var inherited = ForkClientHistory(caller, history, fork);
        var start = inherited.Count;
        inherited.Add(new Dictionary<string, object?>
        {
            ["type"] = "message", ["role"] = "user", ["content"] = message
        });
        var template = WebSearchPayload.DeepCopyObject(binding.Run.Template);
        template["model"] = model;
        var effort = JsonDictionaryValue.String(args, "reasoning_effort");
        if (effort.Length > 0)
        {
            var reasoning = JsonDictionaryValue.Object(template, "reasoning", WebSearchPayload.DeepCopyObject);
            reasoning["effort"] = effort;
            template["reasoning"] = reasoning;
        }
        var child = new MultiAgentRun
        {
            Model = model, Template = template,
            ClientTools = binding.Run.ClientTools.Select(WebSearchPayload.DeepCopy).ToList(),
            MaxConcurrentSubagents = binding.Run.MaxConcurrentSubagents,
            CompactThresholdTokens = binding.Run.CompactThresholdTokens,
            Agents = new(StringComparer.Ordinal)
            {
                ["/root"] = new() { Name = "/root", History = inherited, CurrentTurnStart = start, LastTaskMessage = message }
            }
        };
        try
        {
            var reserved = await clientStore.ReserveAsync(binding, callId, taskName, child, binding.Persist, ct);
            control["agent_name"] = reserved.AgentName;
            binding.Run.ClientControlCalls[callId] = control;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new BadRequestException(error.Message);
        }
    }

    private async Task AcceptClientControlResult(MultiAgentClientBinding binding,
        Dictionary<string, object?> call, Dictionary<string, object?> output, CancellationToken ct)
    {
        var callId = JsonDictionaryValue.String(call, "call_id");
        if (!binding.Run.ClientControlCalls.TryGetValue(callId, out var control)) return;
        var action = JsonDictionaryValue.String(control, "action");
        var result = ReadClientResult(JsonDictionaryValue.Get(output, "output"));
        var success = result is not null && !result.ContainsKey("error") && JsonDictionaryValue.Get(result, "isError") is not true;
        if (action == "spawn_agent")
        {
            success &= JsonDictionaryValue.String(result ?? [], "task_name") == JsonDictionaryValue.String(control, "agent_name");
            await clientStore.CompleteSpawnAsync(binding, callId, success, ct);
        }
        else if (action == "interrupt_agent" && success)
        {
            var args = JsonDictionaryValue.Object(control, "arguments", WebSearchPayload.DeepCopyObject);
            var target = await clientStore.TryGetAgentAsync(binding, JsonDictionaryValue.String(args, "target"), ct);
            var activity = JsonDictionaryValue.String(control, "activity_id");
            if (target is not null && target.Id == JsonDictionaryValue.String(control, "target_id") && activity.Length > 0)
                await clientStore.CancelActivityAsync(target, activity);
        }
        binding.Run.ClientControlCalls.Remove(callId);
    }

    private async Task RollbackClientCalls(MultiAgentClientBinding binding,
        IReadOnlyList<Dictionary<string, object?>> calls, CancellationToken ct)
    {
        foreach (var call in calls)
        {
            var id = JsonDictionaryValue.String(call, "call_id");
            if (binding.Run.ClientControlCalls.TryGetValue(id, out var control)
                && JsonDictionaryValue.String(control, "action") == "spawn_agent")
                await clientStore.CompleteSpawnAsync(binding, id, false, ct);
            binding.Run.ClientControlCalls.Remove(id);
        }
    }

    private static Dictionary<string, object?>? ReadClientResult(object? output)
    {
        if (output is IReadOnlyDictionary<string, object?> dictionary) return WebSearchPayload.DeepCopyObject(dictionary);
        if (output is List<object?> blocks)
            output = string.Concat(blocks.OfType<IReadOnlyDictionary<string, object?>>()
                .Select(block => JsonDictionaryValue.Get(block, "text") as string ?? ""));
        if (output is not string text) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? (Dictionary<string, object?>)WebSearchPayload.FromJsonElement(document.RootElement)! : null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return null; }
    }

    private static List<object?> ForkClientHistory(MultiAgentState caller, List<object?> history, string fork)
    {
        if (fork is "" or "all") return history.Select(WebSearchPayload.DeepCopy).ToList();
        var instructions = history.OfType<Dictionary<string, object?>>()
            .Where(item => JsonDictionaryValue.String(item, "role") is "system" or "developer")
            .Select(WebSearchPayload.DeepCopy).ToList();
        if (fork == "none") return instructions;
        if (!int.TryParse(fork, out var count) || count < 1)
            throw new BadRequestException("fork_turns must be none, all, or a positive integer.");
        instructions.AddRange(caller.CompletedTurns.TakeLast(count).SelectMany(turn => turn.Items)
            .Where(item => item is not IReadOnlyDictionary<string, object?> value
                || JsonDictionaryValue.String(value, "role") is not ("system" or "developer"))
            .Select(WebSearchPayload.DeepCopy));
        return instructions;
    }
}

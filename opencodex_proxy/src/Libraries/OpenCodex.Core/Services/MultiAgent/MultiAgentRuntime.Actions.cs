using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.MultiAgent;

public sealed partial class MultiAgentRuntime
{
    private async Task ExecuteAction(MultiAgentState caller, Dictionary<string, object?> call, string action, List<object?> forkHistory)
    {
        var callId = Text(call, "call_id");
        await Item(caller.Name, new()
        {
            ["type"] = "multi_agent_call", ["call_id"] = callId, ["action"] = action,
            ["arguments"] = Text(call, "arguments")
        });
        Dictionary<string, object?> args;
        try
        {
            using var document = JsonDocument.Parse(Text(call, "arguments"));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("arguments must be a JSON object");
            args = (Dictionary<string, object?>)WebSearchPayload.FromJsonElement(document.RootElement)!;
        }
        catch (JsonException error)
        {
            var invalid = new { error = error.Message };
            AddToolResult(caller, callId, invalid);
            await ActionOutput(caller.Name, callId, action, invalid);
            return;
        }
        object result;
        switch (action)
        {
            case "spawn_agent":
            {
                var taskName = Text(args, "task_name");
                if (taskName.Length == 0 || taskName.Contains('/') || taskName is "." or "..")
                { result = new { error = "task_name must be one nonempty path segment" }; break; }
                var name = caller.Name + "/" + taskName;
                if (_run.Agents.ContainsKey(name))
                { result = new { error = "agent already exists; use followup_task", task_name = name }; break; }
                var turns = Text(args, "fork_turns");
                if (turns is not ("" or "all" or "none") && (!int.TryParse(turns, out var count) || count < 1))
                { result = new { error = "fork_turns must be none, all, or a positive integer" }; break; }
                var history = Fork(caller, forkHistory, turns);
                var child = new MultiAgentState { Name = name, Parent = caller.Name, History = history, CurrentTurnStart = history.Count, LastTaskMessage = Text(args, "message") };
                _run.Agents.Add(name, child);
                await Send(caller.Name, name, child.LastTaskMessage, "NEW_TASK");
                result = new { task_name = name };
                break;
            }
            case "send_message":
            case "followup_task":
            {
                var target = Resolve(caller, Text(args, "target"));
                if (target is null) { result = new { error = "target agent does not exist" }; break; }
                if (action == "followup_task" && target.Name == "/root")
                { result = new { error = "followup_task requires a non-root agent" }; break; }
                var message = Text(args, "message");
                await Send(caller.Name, target.Name, message, action == "followup_task" ? "NEW_TASK" : "MESSAGE");
                result = new { task_name = target.Name, delivered = true };
                break;
            }
            case "wait_agent":
                if (caller.WaitingCallId is not null)
                { result = new { error = "This agent already has a pending wait." }; break; }
                caller.Status = "waiting";
                caller.WaitingCallId = callId;
                caller.WaitTimeoutMs = WebSearchPayload.ToInt(JsonDictionaryValue.Get(args, "timeout_ms"), 10000);
                caller.WaitUntil = _time.GetUtcNow().AddMilliseconds(Math.Clamp(caller.WaitTimeoutMs, 1, 60000));
                if (caller.Mailbox.Count > 0) await CompleteWait(caller);
                return;
            case "interrupt_agent":
            {
                var target = Resolve(caller, Text(args, "target"));
                if (target is null || target.Name == caller.Name)
                { result = new { error = "interrupt_agent requires another existing agent" }; break; }
                var previous = target.Status;
                target.Status = "interrupted";
                if (_active.TryGetValue(target.Name, out var running)) running.Stop.Cancel();
                if (target.WaitingCallId is not null)
                {
                    AddToolResult(target, target.WaitingCallId, new { interrupted = true });
                    await ActionOutput(target.Name, target.WaitingCallId, "wait_agent", new { interrupted = true });
                    target.WaitingCallId = null;
                    target.WaitUntil = null;
                }
                FinishTask(target, "interrupted");
                result = new { task_name = target.Name, previous_status = previous, status = "interrupted" };
                break;
            }
            case "list_agents":
                var prefix = Text(args, "path_prefix").TrimEnd('/');
                result = new { agents = _run.Agents.Values.Where(a => prefix.Length == 0 || a.Name == prefix || a.Name.StartsWith(prefix + "/", StringComparison.Ordinal)).Select(a => new { task_name = a.Name, parent = a.Parent, status = a.Status, last_task_message = a.LastTaskMessage }).ToList() };
                break;
            default:
                throw new InvalidOperationException($"Unknown internal multi-agent action: {action}");
        }
        AddToolResult(caller, callId, result);
        await ActionOutput(caller.Name, callId, action, result);
    }

    private async Task CompleteWait(MultiAgentState caller)
    {
        var result = new { updated = caller.Mailbox.Count > 0, timed_out = caller.Mailbox.Count == 0 };
        AddToolResult(caller, caller.WaitingCallId!, result);
        await ActionOutput(caller.Name, caller.WaitingCallId!, "wait_agent", result);
        caller.WaitingCallId = null;
        caller.WaitUntil = null;
        caller.Status = _run.PendingCalls.Values.Contains(caller.Name) ? "tool_wait" : "ready";
    }

    private Task ActionOutput(string agent, string callId, string action, object result) => Item(agent, new()
    {
        ["type"] = "multi_agent_call_output", ["call_id"] = callId, ["action"] = action,
        ["output"] = new List<object?> { new Dictionary<string, object?>
        { ["type"] = "output_text", ["text"] = JsonSerializer.Serialize(result), ["annotations"] = new List<object?>(), ["logprobs"] = new List<object?>() } }
    });

    private static void AddToolResult(MultiAgentState agent, string callId, object result) => agent.History.Add(new Dictionary<string, object?>
    { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = JsonSerializer.Serialize(result) });

    private async Task Send(string sender, string recipient, string text, string kind)
    {
        var target = _run.Agents[recipient];
        var message = Message("user", $"Message Type: {kind}\nTask name: {recipient}\nSender: {sender}\nPayload:\n{text}");
        if (kind == "NEW_TASK") QueueTask(target, [message], text);
        else
        {
            target.Mailbox.Add(message);
            if (kind is "FINAL_ANSWER" or "FAILURE" && target.Status == "finalizing") target.Status = "ready";
        }
        if (kind is "FINAL_ANSWER" or "FAILURE" && target.Status == "finalizing") target.Status = "ready";
        await Item(recipient, new()
        {
            ["type"] = "agent_message", ["author"] = sender, ["recipient"] = recipient,
            ["content"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = text } }
        });
    }

    private MultiAgentState? Resolve(MultiAgentState caller, string target)
    {
        var name = target.StartsWith('/') ? target : caller.Name + "/" + target;
        return _run.Agents.GetValueOrDefault(name);
    }

    private static List<object?> Fork(MultiAgentState caller, List<object?> history, string turns)
    {
        if (turns is "" or "all") return history.Select(WebSearchPayload.DeepCopy).ToList();
        var instructions = history.OfType<Dictionary<string, object?>>()
            .Where(i => Text(i, "role") is "system" or "developer").Select(WebSearchPayload.DeepCopy).ToList();
        if (turns == "none") return instructions;
        var count = int.Parse(turns, System.Globalization.CultureInfo.InvariantCulture);
        instructions.AddRange(caller.CompletedTurns.TakeLast(count).SelectMany(turn => turn.Items)
            .Where(i => i is not Dictionary<string, object?> item || Text(item, "role") is not ("system" or "developer"))
            .Select(WebSearchPayload.DeepCopy));
        return instructions;
    }
}

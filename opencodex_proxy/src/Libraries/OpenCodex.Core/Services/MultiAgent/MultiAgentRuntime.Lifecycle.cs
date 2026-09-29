using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.MultiAgent;

public sealed partial class MultiAgentRuntime
{
    private bool HasUnfinishedDescendants(MultiAgentState agent) => _run.Agents.Values.Any(a =>
        a.Name.StartsWith(agent.Name + "/", StringComparison.Ordinal)
        && (a.PendingTasks.Count > 0 || a.Status is not ("completed" or "failed" or "incomplete" or "interrupted")));

    private static void BeginTask(MultiAgentState agent)
    {
        if (agent.TaskStarted) return;
        agent.TaskStarted = true;
        if (agent.CurrentTaskGeneration == 0) agent.CurrentTaskGeneration = agent.Generation;
    }

    private void QueueTask(MultiAgentState agent, List<object?> messages, string description)
    {
        agent.PendingTasks.Add(new MultiAgentPendingTask
        {
            Generation = ++agent.Generation,
            Description = description,
            Messages = messages.Select(WebSearchPayload.DeepCopy).ToList()
        });
        if (!agent.TaskStarted) StartNextTask(agent);
    }

    private bool StartNextTask(MultiAgentState agent)
    {
        if (agent.TaskStarted || agent.PendingTasks.Count == 0) return false;
        var next = agent.PendingTasks[0];
        agent.PendingTasks.RemoveAt(0);
        agent.CurrentTaskGeneration = next.Generation;
        agent.LastTaskMessage = next.Description;
        agent.LastError = null;
        agent.CurrentTurnStart = agent.History.Count;
        agent.CurrentTurnPrefix.Clear();
        agent.Mailbox.AddRange(next.Messages);
        // Reserve this task immediately so multiple queued assignments cannot be merged before scheduling.
        agent.TaskStarted = true;
        agent.Status = _run.PendingCalls.Values.Contains(agent.Name) ? "tool_wait"
            : agent.WaitingCallId is not null ? "waiting" : "ready";
        return true;
    }

    private static void ApplyCompactedHistory(MultiAgentState agent, List<object?> history)
    {
        if (agent.TaskStarted)
            agent.CurrentTurnPrefix.AddRange(agent.History.Skip(agent.CurrentTurnStart).Select(WebSearchPayload.DeepCopy));
        agent.History = history;
        // The summary is context, not another copy of the current task's original transcript.
        agent.CurrentTurnStart = history.Count;
    }

    private void FinishTask(MultiAgentState agent, string status)
    {
        if (!agent.TaskStarted) return;
        agent.CompletedTurns.Add(new MultiAgentTaskTurn
        {
            Generation = agent.CurrentTaskGeneration, Status = status,
            Items = agent.CurrentTurnPrefix.Concat(agent.History.Skip(agent.CurrentTurnStart)).Select(WebSearchPayload.DeepCopy).ToList()
        });
        agent.CurrentTurnPrefix.Clear();
        agent.CurrentTurnStart = agent.History.Count;
        agent.TaskStarted = false;
    }

    private async Task FailAgent(MultiAgentState agent, string status, string message)
    {
        agent.Status = status;
        agent.LastError = message;
        FinishTask(agent, status);
        await Send(agent.Name, agent.Parent, JsonSerializer.Serialize(new
        {
            status, error = message, task = agent.LastTaskMessage, generation = agent.CurrentTaskGeneration
        }), "FAILURE");
        StartNextTask(agent);
    }
}

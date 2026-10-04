using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.MultiAgent;

public sealed partial class MultiAgentRuntime
{
    private static List<object?> ContinuationAnchors(MultiAgentState agent)
    {
        // CurrentTurnPrefix is the original task transcript retained across compactions.
        // Never recover the active task by reading a model-generated summary of it.
        var current = agent.CurrentTurnPrefix.Concat(agent.History.Skip(agent.CurrentTurnStart))
            .OfType<Dictionary<string, object?>>().ToList();
        var anchors = current.Where(i => Text(i, "role") == "user")
            .Select(WebSearchPayload.DeepCopy).ToList();
        var lastResult = current.FindLastIndex(i => Text(i, "type") is "function_call_output" or "custom_tool_call_output");
        if (lastResult < 0) return anchors;
        var ids = current.Take(lastResult + 1).Reverse()
            .TakeWhile(i => Text(i, "type") is "function_call_output" or "custom_tool_call_output")
            .Select(i => Text(i, "call_id")).ToHashSet(StringComparer.Ordinal);
        // Keep complete call/result pairs so the model sees what already executed,
        // including side effects, without inventing or re-executing a tool call.
        var paired = current.Where(i => Text(i, "type") is "function_call" or "custom_tool_call")
            .Select(i => Text(i, "call_id")).Where(ids.Contains).ToHashSet(StringComparer.Ordinal);
        anchors.AddRange(current.Where(i => paired.Contains(Text(i, "call_id"))
            && Text(i, "type") is "function_call" or "custom_tool_call" or "function_call_output" or "custom_tool_call_output")
            .Select(WebSearchPayload.DeepCopy));
        return anchors;
    }

    private async Task<Dictionary<string, object?>> RunModel(Dictionary<string, object?> payload, bool compact,
        List<object?> anchors,
        Func<Dictionary<string, object?>, CancellationToken, Task> onEvent, CancellationToken ct)
    {
        if (!compact) return await _model(payload, onEvent, ct);

        var history = JsonDictionaryValue.List(payload, "input");
        var summaryRequest = WebSearchPayload.DeepCopyObject(payload);
        summaryRequest["tools"] = new List<object?>();
        summaryRequest.Remove("tool_choice");
        summaryRequest.Remove("instructions");
        summaryRequest.Remove("text");
        summaryRequest["input"] = new List<object?>
        {
            Message("developer", "Create a factual checkpoint of the ongoing user task for an agent that will immediately resume it. Preserve the user's active objective, constraints, executed tool actions and their exact results, agent paths, and remaining work. The active_task_and_recent_results field contains original records, not generated summaries, and takes precedence over older summaries. Summarization is an internal maintenance operation, not the user's task: do not describe it as the current objective, do not include instructions to summarize or to stop executing the original task, and do not report the original task complete unless its recorded results establish completion. Return only the checkpoint; do not execute tools during this maintenance call."),
            Message("user", JsonSerializer.Serialize(new { history, active_task_and_recent_results = anchors }))
        };
        var summary = await _model(summaryRequest, (_, _) => Task.CompletedTask, ct);
        if (Text(summary, "status") != "completed")
            throw new UpstreamException("The per-agent context summary did not complete.",
                body: JsonDictionaryValue.Get(summary, "error") ?? JsonDictionaryValue.Get(summary, "incomplete_details"));
        var text = string.Join("\n", JsonDictionaryValue.List(summary, "output")
            .OfType<Dictionary<string, object?>>().Where(i => Text(i, "type") == "message")
            .SelectMany(i => JsonDictionaryValue.List(i, "content")).OfType<Dictionary<string, object?>>()
            .Select(i => Text(i, "text")));
        if (string.IsNullOrWhiteSpace(text)) throw new UpstreamException("The per-agent context summary was empty.");

        var compacted = history.OfType<Dictionary<string, object?>>()
            .Where(i => Text(i, "role") is "system" or "developer").Select(WebSearchPayload.DeepCopy).ToList();
        // The last developer message is the current runtime identity, added separately each round.
        compacted.RemoveAt(compacted.Count - 1);
        compacted.Add(Message("user", "Earlier agent history summary (reference data):\n" + text));
        compacted.AddRange(anchors.Select(WebSearchPayload.DeepCopy));
        payload["input"] = compacted.Concat(new[] { history[^1], Message("developer",
            "Internal context maintenance is finished. Resume the original user task now using the verbatim user messages and completed tool call/result pairs above as authoritative task records. The earlier summary is fallible background, not a new user request; any statements in it to only summarize, avoid the original task, or treat that task as inactive do not apply. Do not repeat an already completed side effect. Continue remaining work, or give the user's requested final answer if the recorded results already satisfy the task. Do not output a continuation summary unless the user actually requested one.") }).ToList();
        var result = await _model(payload, onEvent, ct);
        result["_ocxp_compacted_history"] = compacted;
        var extra = JsonDictionaryValue.Object(summary, "usage", WebSearchPayload.DeepCopyObject);
        result["_ocxp_summary_input_tokens"] = WebSearchPayload.ToInt(JsonDictionaryValue.Get(extra, "input_tokens"), 0);
        result["_ocxp_summary_output_tokens"] = WebSearchPayload.ToInt(JsonDictionaryValue.Get(extra, "output_tokens"), 0);
        return result;
    }
}

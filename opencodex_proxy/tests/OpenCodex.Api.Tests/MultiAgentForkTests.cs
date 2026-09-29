using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using System.Text.Json;
using Xunit;
using static OpenCodex.Api.Tests.MultiAgentTestHarness;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentForkTests
{
    [Fact]
    public async Task CompactionPreservesOriginalTaskTranscript_ForNextCompletedTurnFork()
    {
        var run = Run();
        run.Agents["/root"].History = [Message("KEEP_RULE", "developer"), Message("ORIGINAL_TASK_TEXT", "user")];
        run.CompactThresholdTokens = 1;
        run.Agents["/root"].LastInputTokens = 2;
        var summaries = 0;
        await Execute(run, (payload, ct) =>
        {
            if (JsonDictionaryValue.List(payload, "tools").Count == 0)
            {
                summaries++;
                return Task.FromResult(Response(Message("SUMMARY_REPLACEMENT")));
            }
            Assert.Contains("SUMMARY_REPLACEMENT", History(payload));
            return Task.FromResult(Response(Message("ORIGINAL_TASK_RESULT")));
        });
        Assert.Equal(1, summaries);
        var completed = Assert.Single(run.Agents["/root"].CompletedTurns);
        var transcript = JsonSerializer.Serialize(completed.Items);
        Assert.Contains("ORIGINAL_TASK_TEXT", transcript);
        Assert.Contains("ORIGINAL_TASK_RESULT", transcript);
        Assert.DoesNotContain("SUMMARY_REPLACEMENT", transcript);

        run.CompactThresholdTokens = int.MaxValue;
        var rootCalls = 0;
        string? childInput = null;
        await Execute(run, (payload, ct) =>
        {
            if (Agent(payload) != "/root")
            {
                childInput = History(payload);
                return Task.FromResult(Response(Message("CHILD_DONE")));
            }
            if (++rootCalls == 1) return Task.FromResult(Response(Call("ocxp_ma_spawn_agent", new { task_name = "copy", message = "CHILD_TASK", fork_turns = "1" })));
            return Task.FromResult(History(payload).Contains("CHILD_DONE") ? Response(Message("ROOT_DONE")) : Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 60000 })));
        }, new() { ["input"] = new List<object?> { Message("CURRENT_NEW_TASK", "user") } });
        Assert.NotNull(childInput);
        Assert.Contains("ORIGINAL_TASK_TEXT", childInput);
        Assert.Contains("ORIGINAL_TASK_RESULT", childInput);
        Assert.DoesNotContain("CURRENT_NEW_TASK", childInput);
        Assert.Equal(2, run.Agents["/root"].CompletedTurns.Count);
    }

    [Theory]
    [InlineData("1", false, true)]
    [InlineData("2", true, true)]
    [InlineData("99", true, true)]
    [InlineData("none", false, false)]
    public async Task Fork_uses_completed_task_turns_not_mailbox_or_tool_rounds(string fork, bool first, bool second)
    {
        var run = Run(); var root = run.Agents["/root"];
        var turn1 = new MultiAgentTaskTurn { Generation = 1, Items = [Message("TURN_ONE", "user"), Message("RESULT_ONE")] };
        var turn2 = new MultiAgentTaskTurn { Generation = 2, Items = [Message("TURN_TWO", "user"), Call("tool", new { }), new Dictionary<string, object?> { ["type"] = "function_call_output", ["call_id"] = "upstream-id", ["output"] = "PAIR_RESULT" }, Message("RESULT_TWO")] };
        root.CompletedTurns = [turn1, turn2];
        root.History = [Message("ORIGINAL_RULE", "developer"), .. turn1.Items, .. turn2.Items, Message("CURRENT_TASK", "user"), Message("MAILBOX_IS_NOT_A_TURN", "user")];
        root.CurrentTurnStart = root.History.Count - 2;
        var rootCalls = 0; string? inherited = null;
        await Execute(run, (payload, ct) =>
        {
            if (Agent(payload) != "/root")
            {
                inherited = History(payload);
                return Task.FromResult(Response(Message("CHILD_DONE")));
            }
            rootCalls++;
            if (rootCalls == 1) return Task.FromResult(Response(Call("ocxp_ma_spawn_agent", new { task_name = "a", message = "CHILD_TASK", fork_turns = fork })));
            return Task.FromResult(History(payload).Contains("CHILD_DONE") ? Response(Message("DONE")) : Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 100 })));
        });
        Assert.NotNull(inherited);
        Assert.Contains("ORIGINAL_RULE", inherited);
        Assert.Equal(first, inherited.Contains("TURN_ONE"));
        Assert.Equal(second, inherited.Contains("TURN_TWO"));
        Assert.Equal(second, inherited.Contains("PAIR_RESULT"));
        Assert.DoesNotContain("CURRENT_TASK", inherited);
        Assert.DoesNotContain("MAILBOX_IS_NOT_A_TURN", inherited);
    }
}

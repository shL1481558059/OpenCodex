using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using static OpenCodex.Api.Tests.MultiAgentTestHarness;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentContextTests
{
    [Theory]
    [InlineData(99, 1)]
    [InlineData(100, 2)]
    public async Task SummaryStartsAtConfiguredThresholdAndAccountsForBothCalls(int lastTokens, int expectedCalls)
    {
        var run = Run(); run.CompactThresholdTokens = 100; run.Agents["/root"].LastInputTokens = lastTokens;
        var calls = 0;
        await Execute(run, (payload, ct) => { calls++; return Task.FromResult(Response(Message(calls == 1 ? "SUMMARY" : "FINAL"))); });
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(expectedCalls, run.ModelTurns);
        Assert.Equal(expectedCalls * 10, run.InputTokens);
        Assert.Equal(expectedCalls * 2, run.OutputTokens);
    }

    [Fact]
    public async Task SummaryPreservesSystemDeveloperConstraintsAndIsolatesOtherAgentHistory()
    {
        var run = Run(); var root = run.Agents["/root"]; root.LastInputTokens = run.CompactThresholdTokens;
        root.History.Insert(0, Message("SYSTEM_CONSTRAINT", "system"));
        root.History.Insert(1, Message("DEVELOPER_CONSTRAINT", "developer"));
        root.History.Add(Message("ROOT_PRIVATE"));
        run.Agents["/root/other"] = new() { Name = "/root/other", Parent = "/root", Status = "completed", History = [Message("OTHER_PRIVATE")] };
        var calls = 0;
        await Execute(run, (payload, ct) =>
        {
            calls++;
            Assert.DoesNotContain("OTHER_PRIVATE", History(payload));
            if (calls == 1)
            {
                Assert.Contains("ROOT_PRIVATE", History(payload));
                Assert.Empty(JsonDictionaryValue.List(payload, "tools"));
                Assert.False(payload.ContainsKey("tool_choice"));
                return Task.FromResult(Response(Message("TASK_AND_RESULTS")));
            }
            Assert.Contains("SYSTEM_CONSTRAINT", History(payload));
            Assert.Contains("DEVELOPER_CONSTRAINT", History(payload));
            Assert.Contains("TASK_AND_RESULTS", History(payload));
            Assert.DoesNotContain("ROOT_PRIVATE", History(payload));
            return Task.FromResult(Response(Message("FINAL")));
        });
        Assert.Equal(2, calls);
        Assert.Contains("OTHER_PRIVATE", System.Text.Json.JsonSerializer.Serialize(run.Agents["/root/other"].History));
        Assert.Contains("TASK_AND_RESULTS", System.Text.Json.JsonSerializer.Serialize(root.History));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("incomplete")]
    [InlineData("empty")]
    public async Task InvalidSummaryDoesNotDiscardOriginalHistory(string status)
    {
        var run = Run(); run.Agents["/root"].LastInputTokens = run.CompactThresholdTokens;
        var before = System.Text.Json.JsonSerializer.Serialize(run.Agents["/root"].History);
        var calls = 0;
        var events = await Execute(run, (payload, ct) =>
        {
            calls++;
            var result = status == "empty" ? Response() : Response(Message("PARTIAL"));
            result["status"] = status == "empty" ? "completed" : status;
            return Task.FromResult(result);
        });
        AssertFailed(events, status == "empty" ? "summary was empty" : "summary did not complete");
        Assert.Equal(1, calls);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(run.Agents["/root"].History));
        Assert.False(run.Finished);
    }

    [Fact]
    public async Task SummaryAndContinuationRequireTwoRemainingBudgetSlots()
    {
        var run = Run(); run.Agents["/root"].LastInputTokens = run.CompactThresholdTokens;
        var calls = 0;
        var events = new List<Dictionary<string, object?>>();
        var runtime = new MultiAgentRuntime(run, (payload, ct) => { calls++; return Task.FromResult(Response(Message("unused"))); }, e => { events.Add(e); return Task.CompletedTask; }, () => Task.CompletedTask, 1);
        var response = await runtime.ExecuteAsync(new(), default);
        Assert.Equal("failed", JsonDictionaryValue.String(response, "status"));
        AssertFailed(events, "model-turn budget");
        Assert.Equal(0, calls);
        Assert.Equal(0, run.ModelTurns);
    }

    private static void AssertFailed(List<Dictionary<string, object?>> events, string error)
    {
        var terminal = Assert.Single(events, e => JsonDictionaryValue.String(e, "type") is "response.failed" or "response.completed" or "response.incomplete");
        Assert.Equal("response.failed", JsonDictionaryValue.String(terminal, "type"));
        var response = JsonDictionaryValue.Object(terminal, "response", WebSearchPayload.DeepCopyObject);
        Assert.Equal("failed", JsonDictionaryValue.String(response, "status"));
        Assert.Contains(error, JsonDictionaryValue.String(JsonDictionaryValue.Object(response, "error", WebSearchPayload.DeepCopyObject), "message"));
    }

    [Fact]
    public async Task IncompleteRootResponseHasOneIncompleteTerminalAndCountsUsage()
    {
        var run = Run();
        var events = await Execute(run, (payload, ct) =>
        {
            var result = Response(Message("PARTIAL")); result["status"] = "incomplete";
            result["incomplete_details"] = new Dictionary<string, object?> { ["reason"] = "max_output_tokens" };
            return Task.FromResult(result);
        });
        Assert.Single(events, item => JsonDictionaryValue.String(item, "type") == "response.incomplete");
        Assert.DoesNotContain(events, item => JsonDictionaryValue.String(item, "type") == "response.completed");
        Assert.False(run.Finished);
        Assert.Equal(10, run.InputTokens); Assert.Equal(2, run.OutputTokens);
    }
}

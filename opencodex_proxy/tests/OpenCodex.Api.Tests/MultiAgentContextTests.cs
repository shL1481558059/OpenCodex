using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using static OpenCodex.Api.Tests.MultiAgentTestHarness;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentContextTests
{
    [Theory]
    [InlineData(99, 0, 1)]
    [InlineData(100, 0, 2)]
    [InlineData(99, 1000, 1)]
    [InlineData(100, 1000, 2)]
    public async Task SummaryStartsAtConfiguredThresholdRegardlessOfTurnCount(int lastTokens, int initialTurns, int expectedCalls)
    {
        var run = Run(); run.ModelTurns = initialTurns; run.CompactThresholdTokens = 100; run.Agents["/root"].LastInputTokens = lastTokens;
        var calls = 0;
        await Execute(run, (payload, ct) => { calls++; return Task.FromResult(Response(Message(calls == 1 ? "SUMMARY" : "FINAL"))); });
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(initialTurns + expectedCalls, run.ModelTurns);
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
    public async Task RunCompletesAfterMoreThan128ModelCalls()
    {
        var run = Run();
        var calls = 0;
        var events = new List<Dictionary<string, object?>>();
        var runtime = new MultiAgentRuntime(run, (payload, ct) =>
        {
            return Task.FromResult(++calls < 130
                ? Response(Call("ocxp_ma_list_agents", new { })) : Response(Message("FINAL")));
        }, e => { events.Add(e); return Task.CompletedTask; }, () => Task.CompletedTask);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await runtime.ExecuteAsync(new(), deadline.Token);

        Assert.Equal("completed", JsonDictionaryValue.String(response, "status"));
        Assert.True(run.Finished);
        Assert.Equal(130, calls);
        Assert.Equal(130, run.ModelTurns);
        Assert.Equal(1300, run.InputTokens);
        Assert.Equal(260, run.OutputTokens);
        Assert.Single(events, item => JsonDictionaryValue.String(item, "type") == "response.completed");
        Assert.DoesNotContain(events, item => JsonDictionaryValue.String(item, "type") == "response.failed");
    }

    [Fact]
    public async Task CancellationStillStopsActiveModelAfterMoreThan128Calls()
    {
        var run = Run();
        var calls = 0;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modelCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<Dictionary<string, object?>>();
        var runtime = new MultiAgentRuntime(run, async (payload, ct) =>
        {
            if (++calls < 130) return Response(Call("ocxp_ma_list_agents", new { }));
            blocked.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { modelCancelled.TrySetResult(); throw; }
            return Response(Message("UNREACHABLE"));
        }, e => { events.Add(e); return Task.CompletedTask; }, () => Task.CompletedTask);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var execution = runtime.ExecuteAsync(new(), cancellation.Token);
        try
        {
            var first = await Task.WhenAny(blocked.Task, execution).WaitAsync(deadline.Token);
            Assert.Same(blocked.Task, first);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            await modelCancelled.Task.WaitAsync(deadline.Token);

            Assert.Equal(130, calls);
            Assert.Equal(130, run.ModelTurns);
            Assert.False(run.Finished);
            Assert.Equal("ready", run.Agents["/root"].Status);
            Assert.DoesNotContain(events, item => JsonDictionaryValue.String(item, "type") is "response.completed" or "response.failed");
        }
        finally
        {
            cancellation.Cancel();
            try { await execution; } catch (OperationCanceledException) { }
        }
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

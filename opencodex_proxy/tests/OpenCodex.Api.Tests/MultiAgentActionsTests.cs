using System.Text.Json;
using System.Threading.Channels;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using static OpenCodex.Api.Tests.MultiAgentTestHarness;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentActionsTests
{
    [Fact]
    public async Task InterruptedToolResult_StaysWithOldTask_WhenFollowupWasQueued()
    {
        var run = Run();
        var oldCall = Call("client_read", new { });
        oldCall["call_id"] = "call_ma_old";
        run.Agents["/root/child"] = new()
        {
            Name = "/root/child", Parent = "/root", Status = "tool_wait", TaskStarted = true,
            Generation = 1, CurrentTaskGeneration = 1, LastTaskMessage = "OLD_ASSIGNMENT",
            History = [Message("OLD_ASSIGNMENT", "user"), oldCall]
        };
        run.PendingCalls["call_ma_old"] = "/root/child";
        var rootCalls = 0;
        Task<D> Model(D payload, CancellationToken ct)
        {
            if (Agent(payload) != "/root")
            {
                Assert.Contains("NEW_ASSIGNMENT", History(payload));
                return Task.FromResult(Response(Message("NEW_DONE")));
            }
            if (++rootCalls == 1) return Task.FromResult(Response(
                Call("ocxp_ma_interrupt_agent", new { target = "child" }),
                Call("ocxp_ma_followup_task", new { target = "child", message = "NEW_ASSIGNMENT" })));
            return Task.FromResult(History(payload).Contains("NEW_DONE") ? Response(Message("ROOT_DONE")) : Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 60000 })));
        }
        await Execute(run, Model);
        Assert.Single(run.PendingCalls);
        await Execute(run, Model, new()
        {
            ["input"] = new List<object?> { new D { ["type"] = "function_call_output", ["call_id"] = "call_ma_old", ["output"] = "LATE_OLD_TOOL_RESULT" } }
        });
        var turns = run.Agents["/root/child"].CompletedTurns;
        Assert.Equal(2, turns.Count);
        Assert.Equal("interrupted", turns[0].Status);
        Assert.Contains("LATE_OLD_TOOL_RESULT", JsonSerializer.Serialize(turns[0].Items));
        Assert.DoesNotContain("LATE_OLD_TOOL_RESULT", JsonSerializer.Serialize(turns[1].Items));
        Assert.Contains("NEW_ASSIGNMENT", JsonSerializer.Serialize(turns[1].Items));
    }

    [Fact]
    public async Task SpawnDuplicate_ReturnsToolError_AndForkNoneKeepsOnlyInstructions()
    {
        var run = Run();
        run.Agents["/root"].History.Add(Message("PRESERVE_CONSTRAINT", "developer"));
        run.Agents["/root"].History.Add(Message("PRIVATE_ANCESTOR", "user"));
        var rootCalls = 0;
        var children = 0;
        await Execute(run, (payload, ct) =>
        {
            if (Agent(payload) != "/root")
            {
                children++;
                Assert.Contains("PRESERVE_CONSTRAINT", History(payload));
                Assert.DoesNotContain("PRIVATE_ANCESTOR", History(payload));
                Assert.Contains("CHILD_ASSIGNMENT", History(payload));
                return Task.FromResult(Response(Message("CHILD_DONE")));
            }
            if (++rootCalls == 1) return Task.FromResult(Response(
                Call("ocxp_ma_spawn_agent", new { task_name = "child", message = "CHILD_ASSIGNMENT", fork_turns = "none" }),
                Call("ocxp_ma_spawn_agent", new { task_name = "child", message = "DUPLICATE", fork_turns = "none" })));
            return Task.FromResult(History(payload).Contains("CHILD_DONE") ? Response(Message("ROOT_DONE")) : Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 60000 })));
        });
        Assert.Equal(2, run.Agents.Count);
        Assert.Equal(1, children);
        Assert.Contains("agent already exists", JsonSerializer.Serialize(run.Agents["/root"].History));
    }

    [Fact]
    public async Task SendMessage_DoesNotStartCompletedAgent()
    {
        var run = Run();
        run.Agents["/root/idle"] = new() { Name = "/root/idle", Parent = "/root", Status = "completed" };
        var calls = 0;
        await Execute(run, (payload, ct) =>
        {
            Assert.Equal("/root", Agent(payload));
            return Task.FromResult(++calls == 1
                ? Response(Call("ocxp_ma_send_message", new { target = "idle", message = "QUEUED_ONLY" }))
                : Response(Message("DONE")));
        });
        Assert.Equal("completed", run.Agents["/root/idle"].Status);
        Assert.Contains("QUEUED_ONLY", JsonSerializer.Serialize(Assert.Single(run.Agents["/root/idle"].Mailbox)));
    }

    [Fact]
    public async Task Followup_StartsCompletedAgent_AndDeliversItsResultToParent()
    {
        var run = Run();
        run.Agents["/root/idle"] = new() { Name = "/root/idle", Parent = "/root", Status = "completed" };
        var rootCalls = 0;
        var childCalls = 0;
        await Execute(run, (payload, ct) =>
        {
            if (Agent(payload) != "/root")
            {
                childCalls++;
                Assert.Contains("NEW_ASSIGNMENT", History(payload));
                return Task.FromResult(Response(Message("FOLLOWUP_DONE")));
            }
            if (++rootCalls == 1) return Task.FromResult(Response(Call("ocxp_ma_followup_task", new { target = "idle", message = "NEW_ASSIGNMENT" })));
            return Task.FromResult(History(payload).Contains("FOLLOWUP_DONE") ? Response(Message("ROOT_DONE")) : Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 60000 })));
        });
        Assert.Equal(1, childCalls);
        Assert.True(run.Finished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowupDuringRunning_AndAfterInterrupt_ExecutesNewGeneration(bool interrupt)
    {
        var run = Run();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootCalls = 0;
        var childCalls = 0;
        var cancelled = false;
        await Execute(run, async (payload, ct) =>
        {
            if (Agent(payload) != "/root")
            {
                if (++childCalls == 1)
                {
                    try
                    {
                        if (interrupt) await Task.Delay(Timeout.Infinite, ct);
                        else await release.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException) { cancelled = true; throw; }
                    return Response(Message("OLD_RESULT"));
                }
                Assert.Contains("REVISED_TASK", History(payload));
                return Response(Message("NEW_RESULT"));
            }
            rootCalls++;
            if (rootCalls == 1) return Response(Call("ocxp_ma_spawn_agent", new { task_name = "child", message = "FIRST_TASK", fork_turns = "none" }));
            if (rootCalls == 2) return interrupt
                ? Response(Call("ocxp_ma_interrupt_agent", new { target = "child" }), Call("ocxp_ma_followup_task", new { target = "child", message = "REVISED_TASK" }))
                : Response(Call("ocxp_ma_followup_task", new { target = "child", message = "REVISED_TASK" }));
            release.TrySetResult();
            return History(payload).Contains("NEW_RESULT") ? Response(Message("ROOT_DONE")) : Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 60000 }));
        });
        Assert.Equal(2, childCalls);
        Assert.Equal(interrupt, cancelled);
        Assert.True(run.Finished);
    }

    [Fact]
    public async Task ListPrefix_IncludesSubtree_ButExcludesSimilarSiblingName()
    {
        var run = Run();
        foreach (var name in new[] { "/root/a", "/root/a/child", "/root/ab" })
            run.Agents[name] = new() { Name = name, Parent = "/root", Status = "completed" };
        var calls = 0;
        await Execute(run, (payload, ct) => Task.FromResult(++calls == 1
            ? Response(Call("ocxp_ma_list_agents", new { path_prefix = "/root/a/" })) : Response(Message("DONE"))));
        var output = Assert.Single(run.Agents["/root"].History.OfType<D>(), i => JsonDictionaryValue.String(i, "type") == "function_call_output");
        using var parsed = JsonDocument.Parse(JsonDictionaryValue.String(output, "output"));
        Assert.Equal(new[] { "/root/a", "/root/a/child" }, parsed.RootElement.GetProperty("agents").EnumerateArray().Select(a => a.GetProperty("task_name").GetString()));
    }

    [Fact]
    public async Task ConcurrentLimit_StartsThirdChildOnlyAfterOneSlotCompletes()
    {
        var run = Run();
        run.MaxConcurrentSubagents = 2;
        var started = Channel.CreateUnbounded<string>();
        var releases = new Dictionary<string, TaskCompletionSource>();
        foreach (var name in new[] { "/root/a", "/root/b", "/root/c" })
        {
            run.Agents[name] = new() { Name = name, Parent = "/root" };
            releases[name] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var active = 0;
        var peak = 0;
        var execution = Execute(run, async (payload, ct) =>
        {
            var name = Agent(payload);
            if (name == "/root") return Response(Call("client_tool", new { }));
            var count = Interlocked.Increment(ref active);
            peak = Math.Max(peak, count);
            await started.Writer.WriteAsync(name, ct);
            await releases[name].Task.WaitAsync(ct);
            Interlocked.Decrement(ref active);
            return Response(Message(name + " done"));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var first = await started.Reader.ReadAsync(deadline.Token);
            var second = await started.Reader.ReadAsync(deadline.Token);
            Assert.False(started.Reader.TryRead(out _));
            releases[first].SetResult();
            var third = await started.Reader.ReadAsync(deadline.Token);
            Assert.NotEqual(first, third);
            Assert.NotEqual(second, third);
            releases[second].SetResult();
            releases[third].SetResult();
            await execution;
            Assert.Equal(2, peak);
        }
        finally { foreach (var release in releases.Values) release.TrySetResult(); }
    }
}

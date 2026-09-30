using System.Text.Json;
using System.Threading.Channels;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using static OpenCodex.Api.Tests.MultiAgentTestHarness;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentWaitTests
{
    [Fact]
    public async Task WebSocketPendingTool_DoesNotSuppressAnotherAgentsWaitTimeout()
    {
        var run = Run();
        run.Agents["/root/child"] = new() { Name = "/root/child", Parent = "/root" };
        var clock = new MultiAgentTimeProvider();
        var inner = Channel.CreateUnbounded<MultiAgentInjection>();
        var reader = new ObservedReader(inner.Reader, run);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootCalls = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runtime = Runtime(run, clock, (payload, ct) =>
        {
            if (Agent(payload) != "/root") return Task.FromResult(Response(Call("client_read", new { })));
            if (++rootCalls > 1) resumed.TrySetResult();
            return Task.FromResult(Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 10000 })));
        });
        var execution = runtime.ExecuteAsync(new(), deadline.Token, reader);
        try
        {
            await reader.WaitingOnlyForInjection.Task.WaitAsync(deadline.Token);
            clock.Advance(TimeSpan.FromSeconds(10));
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(2, rootCalls);
            Assert.Single(run.PendingCalls);
        }
        finally
        {
            deadline.Cancel();
            try { await execution; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task WaitTimeout_UsesInjectedClock_AndCompletesMatchingToolCall()
    {
        var run = Run();
        var clock = new MultiAgentTimeProvider();
        var calls = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runtime = Runtime(run, clock, (payload, ct) => Task.FromResult(++calls == 1
            ? Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 10000 })) : Response(Message("DONE"))));
        var execution = runtime.ExecuteAsync(new(), deadline.Token);
        await clock.WaitForTimerAsync(deadline.Token);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(10));
        await execution;
        Assert.Equal(2, calls);
        Assert.Null(run.Agents["/root"].WaitingCallId);
        Assert.True(Result(run, "/root").GetProperty("timed_out").GetBoolean());
        Assert.False(Result(run, "/root").GetProperty("updated").GetBoolean());
        AssertPairs(run.Agents["/root"]);
    }

    [Fact]
    public async Task ChildMessage_WakesParentWithoutAdvancingClock()
    {
        var run = Run();
        run.Agents["/root/child"] = new() { Name = "/root/child", Parent = "/root" };
        var clock = new MultiAgentTimeProvider();
        var initialTime = clock.GetUtcNow();
        var calls = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Runtime(run, clock, (payload, ct) =>
        {
            if (Agent(payload) != "/root") return Task.FromResult(Response(Message("CHILD_REPORT")));
            if (++calls == 1) return Task.FromResult(Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 60000 })));
            Assert.Contains("CHILD_REPORT", History(payload));
            return Task.FromResult(Response(Message("DONE")));
        }).ExecuteAsync(new(), deadline.Token);
        Assert.Equal(initialTime, clock.GetUtcNow());
        Assert.True(Result(run, "/root").GetProperty("updated").GetBoolean());
        Assert.False(Result(run, "/root").GetProperty("timed_out").GetBoolean());
        Assert.True(run.Finished);
    }

    [Fact]
    public async Task InterruptWaitingAgent_ClosesWaitCallAndLeavesItInterrupted()
    {
        var run = Run();
        var clock = new MultiAgentTimeProvider();
        var waitingCall = Call("ocxp_ma_wait_agent", new { timeout_ms = 60000 });
        waitingCall["call_id"] = "wait-child";
        run.Agents["/root/child"] = new()
        {
            Name = "/root/child", Parent = "/root", Status = "waiting",
            WaitingCallId = "wait-child", WaitUntil = clock.GetUtcNow().AddMinutes(1), History = [waitingCall]
        };
        var calls = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Runtime(run, clock, (payload, ct) =>
        {
            Assert.Equal("/root", Agent(payload));
            return Task.FromResult(++calls == 1 ? Response(Call("ocxp_ma_interrupt_agent", new { target = "child" })) : Response(Message("DONE")));
        }).ExecuteAsync(new(), deadline.Token);
        var child = run.Agents["/root/child"];
        Assert.Equal("interrupted", child.Status);
        Assert.Null(child.WaitingCallId);
        Assert.Null(child.WaitUntil);
        Assert.True(Result(run, child.Name).GetProperty("interrupted").GetBoolean());
        AssertPairs(child);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientToolAndWaitInOneTurn_PreserveBothObligations(bool clientFirst)
    {
        var run = Run();
        var clock = new MultiAgentTimeProvider();
        var calls = 0;
        Task<D> Model(D payload, CancellationToken ct)
        {
            if (++calls > 1) return Task.FromResult(Response(Message("DONE")));
            var wait = Call("ocxp_ma_wait_agent", new { timeout_ms = 10000 });
            var client = Call("client_read", new { });
            return Task.FromResult(clientFirst ? Response(client, wait) : Response(wait, client));
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Runtime(run, clock, Model).ExecuteAsync(new(), deadline.Token);
        Assert.False(run.Finished);
        Assert.Equal(1, calls);
        var id = Assert.Single(run.PendingCalls).Key;
        Assert.NotNull(run.Agents["/root"].WaitingCallId);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Runtime(run, clock, Model).ExecuteAsync(new D
        {
            ["input"] = new List<object?> { new D { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = "CLIENT_RESULT" } }
        }, deadline.Token);
        Assert.True(run.Finished);
        Assert.Equal(2, calls);
        Assert.Empty(run.PendingCalls);
        Assert.Null(run.Agents["/root"].WaitingCallId);
        AssertPairs(run.Agents["/root"]);
    }

    [Fact]
    public async Task DuplicateWait_ReturnsErrorWithoutOrphaningOriginalWait()
    {
        var run = Run();
        var clock = new MultiAgentTimeProvider();
        var calls = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var execution = Runtime(run, clock, (payload, ct) => Task.FromResult(++calls == 1
            ? Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 1000 }), Call("ocxp_ma_wait_agent", new { timeout_ms = 1000 }))
            : Response(Message("DONE")))).ExecuteAsync(new(), deadline.Token);
        await clock.WaitForTimerAsync(deadline.Token);
        clock.Advance(TimeSpan.FromSeconds(1));
        await execution;
        Assert.Contains("already has a pending wait", JsonSerializer.Serialize(run.Agents["/root"].History));
        AssertPairs(run.Agents["/root"]);
    }

    private static MultiAgentRuntime Runtime(MultiAgentRun run, TimeProvider clock, Func<D, CancellationToken, Task<D>> model)
        => new(run, model, _ => Task.CompletedTask, () => Task.CompletedTask, clock);

    private static JsonElement Result(MultiAgentRun run, string name)
    {
        var output = Assert.Single(run.Agents[name].History.OfType<D>(), i => JsonDictionaryValue.String(i, "type") == "function_call_output");
        using var document = JsonDocument.Parse(JsonDictionaryValue.String(output, "output"));
        return document.RootElement.Clone();
    }

    private static void AssertPairs(MultiAgentState agent)
    {
        var calls = agent.History.OfType<D>().Where(i => JsonDictionaryValue.String(i, "type") == "function_call").Select(i => JsonDictionaryValue.String(i, "call_id")).Order().ToArray();
        var outputs = agent.History.OfType<D>().Where(i => JsonDictionaryValue.String(i, "type") == "function_call_output").Select(i => JsonDictionaryValue.String(i, "call_id")).Order().ToArray();
        Assert.Equal(calls, outputs);
        Assert.Equal(calls.Length, calls.Distinct().Count());
    }

    private sealed class ObservedReader(ChannelReader<MultiAgentInjection> inner, MultiAgentRun run) : ChannelReader<MultiAgentInjection>
    {
        public TaskCompletionSource WaitingOnlyForInjection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool TryRead(out MultiAgentInjection item) => inner.TryRead(out item!);
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            if (run.PendingCalls.Count > 0 && run.Agents["/root"].WaitingCallId is not null
                && run.Agents.Values.All(agent => agent.Status != "running")) WaitingOnlyForInjection.TrySetResult();
            return inner.WaitToReadAsync(cancellationToken);
        }
    }
}

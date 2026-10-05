using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;
using static OpenCodex.Api.Tests.MultiAgentApiTestContext;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentClientResponseTests
{
    [Fact]
    public async Task NativeToolsWithoutThreadIdentityCannotSilentlyBecomeLegacyAgents()
    {
        using var context = new MultiAgentApiTestContext(ctx => Stream(ctx, Events(Terminal(Message("unexpected legacy")))));
        var error = await Assert.ThrowsAsync<BadRequestException>(() => context.Service.Responses(ClientRequest()));
        Assert.Contains("thread", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.FakeEndpoint.Calls);
    }

    [Fact]
    public async Task NativeIdentityWithoutSpawnDeclarationKeepsClientOwnership()
    {
        using var root = Root(ctx => Stream(ctx, Events(Terminal(Message("NATIVE")))));
        var request = ClientRequest();
        request["tools"] = new List<object?>();
        var response = await Complete(root, request);
        Assert.Contains("NATIVE", JsonSerializer.Serialize(response));
        Assert.Empty(JsonDictionaryValue.List(root.FakeEndpoint.Calls.Single().Payload!, "tools"));
        Assert.NotNull(await root.NativeStore.TryGetAsync(root.Key, "root-thread", default));
        Assert.Null(await root.ClientStore.TryGetAsync(root.Key, "root-thread", "root-thread", default));
    }

    [Fact]
    public async Task LateInterruptResultDoesNotCancelANewerChildRequest()
    {
        var calls = 0;
        using var root = Root(ctx => Stream(ctx, Events(Terminal(++calls switch
        {
            1 => Spawn("spawn", "audit", "fake", "audit"),
            2 => NativeCall("interrupt", "interrupt_agent", new D { ["target"] = "/root/audit" }),
            _ => Message("DONE")
        }))));
        var first = await Complete(root, ClientRequest());
        using var child = Child(root, "audit", "child-thread", ctx => Stream(ctx, Events(Terminal(Message("CHILD")))));
        await Complete(child, ClientRequest());
        var session = (await root.NativeStore.TryGetAsync(root.Key, "child-thread", default))!;
        await using var oldActivity = await root.NativeStore.AcquireRequestAsync(session, default);
        var interrupt = await Complete(root, Continue(first, SpawnResult(Output(first).Single(), "/root/audit")));
        await oldActivity.CancelAsync();
        await oldActivity.DisposeAsync();
        await using var newer = await root.NativeStore.AcquireRequestAsync(session, default);
        await Complete(root, Continue(interrupt, new D
        {
            ["type"] = "function_call_output", ["call_id"] = Output(interrupt).Single()["call_id"],
            ["output"] = "{\"task_name\":\"/root/audit\",\"status\":\"interrupted\"}"
        }));
        Assert.False(newer.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ReplacingToolsPreservesEarlierSpawnFailureInClientHistory()
    {
        var calls = 0;
        using var root = Root(ctx => Stream(ctx, Events(Terminal(++calls == 1
            ? Spawn("spawn", "audit", "fake", "audit") : Message("DONE")))));
        var first = await Complete(root, ClientRequest());
        var next = Continue(first, new D
        {
            ["type"] = "function_call_output", ["call_id"] = Output(first).Single()["call_id"],
            ["output"] = "{\"error\":\"native spawn rejected\"}"
        });
        next["tools"] = new List<object?>();
        await Complete(root, next);
        var payload = root.FakeEndpoint.Calls.Last().Payload!;
        Assert.Empty(JsonDictionaryValue.List(payload, "tools"));
        Assert.Contains("native spawn rejected", JsonSerializer.Serialize(payload["input"]));
        Assert.Contains(JsonDictionaryValue.List(payload, "input").OfType<D>(), item =>
            Type(item) == "function_call" && Equals(item["call_id"], "spawn"));
        Assert.Null(await root.ClientStore.TryGetAsync(root.Key, "root-thread", "root-thread", default));
    }

    [Fact]
    public async Task NativeSpawnBatchRemainsClientOwnedWithoutServerReservations()
    {
        using var root = Root(ctx => Stream(ctx, Events(Terminal(
            Spawn("one", "audit", "fake", "one"), Spawn("two", "audit", "fake", "two")))));
        var response = await Complete(root, ClientRequest());
        Assert.Equal("completed", response["status"]);
        Assert.Equal(new[] { "one", "two" }, Output(response).Select(item => (string)item["call_id"]!));
        Assert.All(Output(response), call => Assert.Equal("spawn_agent", call["name"]));
        Assert.Null(await root.ClientStore.TryGetAsync(root.Key, "root-thread", "root-thread", default));
        Assert.Null(await root.NativeStore.TryGetAsync(root.Key, "child-thread", default));
        Assert.Single(root.FakeEndpoint.Calls);
    }

    [Fact]
    public async Task NativeSpawnForwardsUnchangedAndChildUsesActualClientModelAndHistory()
    {
        using var root = Root(ctx => Stream(ctx, Events(Terminal(Spawn("spawn_a", "audit", "gpt-6-luna", "audit source")))));
        var response = await Complete(root, ClientRequest());
        var spawn = Assert.Single(Output(response));
        Assert.Equal("spawn_a", spawn["call_id"]);
        Assert.Equal("function_call", Type(spawn));
        Assert.Equal("collaboration", spawn["namespace"]);
        Assert.Equal("spawn_agent", spawn["name"]);
        Assert.Contains("gpt-6-luna", (string)spawn["arguments"]!);
        Assert.Null(await root.ClientStore.TryGetAsync(root.Key, "root-thread", "root-thread", default));
        Assert.Null(await root.NativeStore.TryGetAsync(root.Key, "child-thread", default));
        using var child = Child(root, "audit", "child-thread", ctx => Stream(ctx, Events(Terminal(Message("CHILD DONE")))));
        child.CatalogEnabled = false;
        var childResponse = await Complete(child, ClientRequest("client-selected-model", "CLIENT AUTHORITATIVE HISTORY"));
        Assert.Contains("CHILD DONE", JsonSerializer.Serialize(childResponse));
        var payload = child.FakeEndpoint.Calls.Single().Payload!;
        Assert.Equal("client-selected-model", payload["model"]);
        Assert.Contains("CLIENT AUTHORITATIVE HISTORY", JsonSerializer.Serialize(payload["input"]));
        Assert.DoesNotContain("audit source", JsonSerializer.Serialize(payload["input"]));
        Assert.DoesNotContain("ocxp_ma_", JsonSerializer.Serialize(payload["tools"]));
        var session = Assert.IsType<NativeClientSession>(await root.NativeStore.TryGetAsync(root.Key, "child-thread", default));
        Assert.Equal("/root/audit", session.AgentName);
        Assert.Equal("root-thread", session.ParentThreadId);
        Assert.Single(root.FakeEndpoint.Calls);
    }

    [Fact]
    public async Task TwoChildrenKeepModelsHistoryAndOutputInTheirOwnClientResponses()
    {
        using var root = Root(ctx => Stream(ctx, Events(Terminal(
            Spawn("spawn_a", "alpha", "model-alpha", "ALPHA PRIVATE TASK"),
            Spawn("spawn_b", "beta", "model-beta", "BETA PRIVATE TASK")))));
        var rootResponse = await Complete(root, ClientRequest());
        Assert.Equal(2, Output(rootResponse).Count);
        using var alpha = Child(root, "alpha", "thread-alpha", ctx => Stream(ctx, Events(Terminal(Message("ALPHA RESULT")))));
        using var beta = Child(root, "beta", "thread-beta", ctx => Stream(ctx, Events(Terminal(Message("BETA RESULT")))));
        var responses = await Task.WhenAll(Complete(alpha, ClientRequest("model-alpha", "ALPHA PRIVATE TASK")),
            Complete(beta, ClientRequest("model-beta", "BETA PRIVATE TASK")));
        Assert.Contains("ALPHA RESULT", JsonSerializer.Serialize(responses[0]));
        Assert.DoesNotContain("BETA RESULT", JsonSerializer.Serialize(responses[0]));
        Assert.Contains("BETA RESULT", JsonSerializer.Serialize(responses[1]));
        Assert.DoesNotContain("ALPHA RESULT", JsonSerializer.Serialize(responses[1]));
        var alphaPayload = alpha.FakeEndpoint.Calls.Single().Payload!;
        var betaPayload = beta.FakeEndpoint.Calls.Single().Payload!;
        Assert.Equal("model-alpha", alphaPayload["model"]);
        Assert.Equal("model-beta", betaPayload["model"]);
        Assert.DoesNotContain("BETA PRIVATE TASK", JsonSerializer.Serialize(alphaPayload["input"]));
        Assert.DoesNotContain("ALPHA PRIVATE TASK", JsonSerializer.Serialize(betaPayload["input"]));
        Assert.Single(root.FakeEndpoint.Calls);
    }

    [Fact]
    public async Task ChildCanCompleteWhileParentIsWaitingForItsOwnModelRequest()
    {
        var parentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var root = Root(async ctx =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return await Stream(ctx, Events(Terminal(Spawn("spawn_a", "audit", "fake", "audit"))));
            parentEntered.SetResult();
            await releaseParent.Task.WaitAsync(ctx.CancellationToken);
            return await Stream(ctx, Events(Terminal(Message("PARENT DONE"))));
        });
        var first = await Complete(root, ClientRequest());
        var continuation = Continue(first, SpawnResult(Output(first).Single(), "/root/audit"));
        var parentOperation = Complete(root, continuation);
        await parentEntered.Task.WaitAsync(root.Lifetime.Token);
        using var child = Child(root, "audit", "child-thread", ctx => Stream(ctx, Events(Terminal(Message("CHILD DONE")))));
        try
        {
            var childResponse = await Complete(child, ClientRequest("fake", "audit")).WaitAsync(root.Lifetime.Token);
            Assert.Contains("CHILD DONE", JsonSerializer.Serialize(childResponse));
            Assert.False(parentOperation.IsCompleted);
        }
        finally { releaseParent.TrySetResult(); }
        Assert.Contains("PARENT DONE", JsonSerializer.Serialize(await parentOperation));
    }

    [Fact]
    public async Task NativeSpawnResultResumesParentAndRetainsNativeToolHistory()
    {
        var count = 0;
        using var root = Root(ctx => Stream(ctx, Events(Terminal(++count == 1
            ? Spawn("spawn_a", "audit", "fake", "audit") : Message("CONTINUED")))));
        var first = await Complete(root, ClientRequest());
        var tool = Output(first).Single();
        var second = await Complete(root, Continue(first, SpawnResult(tool, "/root/audit")));
        Assert.Contains("CONTINUED", JsonSerializer.Serialize(second));
        Assert.Null(await root.ClientStore.TryGetAsync(root.Key, "root-thread", "root-thread", default));
        var history = JsonDictionaryValue.List(root.FakeEndpoint.Calls.Last().Payload!, "input").OfType<D>().ToList();
        Assert.Contains(history, item => Type(item) == "function_call" && JsonDictionaryValue.String(item, "namespace") == "collaboration");
        Assert.Contains(history, item => Type(item) == "function_call_output" && JsonDictionaryValue.String(item, "call_id") == (string)tool["call_id"]!);
    }

    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("wrong-parent")]
    [InlineData("second-thread")]
    public async Task ClientThreadRejectsCrossOwnerContinuationAndConflictingIdentity(string mismatch)
    {
        using var root = Root(ctx => Stream(ctx, Events(Terminal(Message("ROOT")))));
        await Complete(root, ClientRequest());
        using var legitimate = Child(root, "audit", "first-child", ctx => Stream(ctx, Events(Terminal(Message("DONE")))));
        var previous = await Complete(legitimate, ClientRequest("child-model", "audit"));
        using var child = new MultiAgentApiTestContext(_ => throw new Exception("Invalid continuation must not call upstream"),
            root.Store, mismatch == "wrong-owner" ? Guid.NewGuid() : root.Key, "root-thread", root.ClientStore);
        Identify(child, mismatch == "second-thread" ? "second-child" : "first-child",
            mismatch == "wrong-parent" ? "another-parent" : "root-thread", "/root/audit");
        var next = ClientRequest("child-model", "audit");
        next["previous_response_id"] = previous["id"];
        await Assert.ThrowsAsync<BadRequestException>(() => child.Service.Responses(next));
        Assert.Empty(child.FakeEndpoint.Calls);
    }

    [Fact]
    public async Task ClientChildCanChangeModelOnItsNextRequest()
    {
        using var root = Root(ctx => Stream(ctx, Events(Terminal(Message("ROOT")))));
        await Complete(root, ClientRequest());
        using var child = Child(root, "audit", "child-thread", ctx => Stream(ctx, Events(Terminal(Message("DONE")))));
        var previous = await Complete(child, ClientRequest("first-model", "audit"));
        var next = ClientRequest("next-model", "followup");
        next["previous_response_id"] = previous["id"];
        await Complete(child, next);
        Assert.Equal("next-model", child.FakeEndpoint.Calls.Last().Payload!["model"]);
    }

    [Fact]
    public async Task PreviousResponseCanReconnectOnlyToTheSameClientActor()
    {
        var count = 0;
        using var root = Root(ctx => Stream(ctx, Events(Terminal(++count == 1
            ? Spawn("spawn_a", "audit", "fake", "audit") : Message("ROOT")))));
        var rootFirst = await Complete(root, ClientRequest());
        using var child = Child(root, "audit", "child-thread", ctx => Stream(ctx, Events(Terminal(Message("CHILD")))));
        var childFirst = await Complete(child, ClientRequest("fake", "audit"));
        using var reconnect = Child(root, "audit", "child-thread", ctx => Stream(ctx, Events(Terminal(Message("RECONNECTED")))));
        var next = ClientRequest("fake", "followup");
        next["previous_response_id"] = childFirst["id"];
        Assert.Contains("RECONNECTED", JsonSerializer.Serialize(await Complete(reconnect, next)));
        next["previous_response_id"] = rootFirst["id"];
        await Assert.ThrowsAsync<BadRequestException>(() => reconnect.Service.Responses(next));
        Assert.Single(reconnect.FakeEndpoint.Calls);
    }

    [Fact]
    public async Task AgentMessageResumesCompletedChildWithoutCreatingAnotherServerAgent()
    {
        var rootCount = 0;
        using var root = Root(ctx => Stream(ctx, Events(Terminal(++rootCount == 1
            ? Spawn("spawn_a", "audit", "fake", "audit")
            : NativeCall("followup_a", "followup_task", new D { ["target"] = "/root/audit", ["message"] = "audit second area" })))));
        var rootFirst = await Complete(root, ClientRequest());
        var count = 0;
        using var child = Child(root, "audit", "child-thread", ctx => Stream(ctx, Events(Terminal(Message(++count == 1 ? "FIRST" : "FOLLOWUP")))));
        var first = await Complete(child, ClientRequest("fake", "audit"));
        var followup = Output(await Complete(root, Continue(rootFirst,
            SpawnResult(Output(rootFirst).Single(), "/root/audit")))).Single();
        Assert.Equal("followup_task", followup["name"]);
        Assert.Equal("collaboration", followup["namespace"]);
        var next = ClientRequest();
        next["previous_response_id"] = first["id"];
        next["input"] = new List<object?> { new D
        {
            ["type"] = "agent_message", ["id"] = "followup-1", ["author"] = "/root", ["recipient"] = "/root/audit", ["content"] = "audit second area"
        } };
        var response = await Complete(child, next);
        Assert.Contains("FOLLOWUP", JsonSerializer.Serialize(response));
        Assert.Contains("audit second area", JsonSerializer.Serialize(child.FakeEndpoint.Calls.Last().Payload!["input"]));
        var session = Assert.IsType<NativeClientSession>(await root.NativeStore.TryGetAsync(root.Key, "child-thread", default));
        Assert.Equal("/root/audit", session.AgentName);
        Assert.Null(await root.ClientStore.TryGetAsync(root.Key, "root-thread", "child-thread", default));
    }

    [Fact]
    public async Task ClientCancellationStopsOnlyTargetHttpRequestAndAllowsLaterFollowup()
    {
        var rootCount = 0;
        using var root = Root(ctx => Stream(ctx, Events(++rootCount switch
        {
            1 => Terminal(Spawn("spawn_a", "alpha", "fake", "alpha"), Spawn("spawn_b", "beta", "fake", "beta")),
            2 => Terminal(NativeCall("interrupt_a", "interrupt_agent", new D { ["target"] = "/root/alpha" })),
            _ => Terminal(Message("PARENT DONE"))
        })));
        var rootFirst = await Complete(root, ClientRequest());
        var spawns = Output(rootFirst);
        var alphaEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var betaEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBeta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var alpha = Child(root, "alpha", "thread-alpha", async ctx =>
        {
            alphaEntered.TrySetResult(ctx.CancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
            throw new InvalidOperationException("Cancelled model request cannot complete");
        });
        using var beta = Child(root, "beta", "thread-beta", async ctx =>
        {
            betaEntered.TrySetResult(ctx.CancellationToken);
            await releaseBeta.Task.WaitAsync(ctx.CancellationToken);
            return await Stream(ctx, Events(Terminal(Message("BETA DONE"))));
        });
        var alphaOperation = alpha.Service.Responses(ClientRequest("fake", "alpha"));
        var betaOperation = Complete(beta, ClientRequest("fake", "beta"));
        var alphaToken = await alphaEntered.Task.WaitAsync(root.Lifetime.Token);
        var betaToken = await betaEntered.Task.WaitAsync(root.Lifetime.Token);
        try
        {
            var interruptResponse = await Complete(root, Continue(rootFirst,
                SpawnResult(spawns[0], "/root/alpha"), SpawnResult(spawns[1], "/root/beta")));
            var interruptResult = new D
            {
                ["type"] = "function_call_output", ["call_id"] = Output(interruptResponse).Single()["call_id"],
                ["output"] = "{\"task_name\":\"/root/alpha\",\"status\":\"interrupted\"}"
            };
            await Complete(root, Continue(interruptResponse, interruptResult));
            Assert.False(alphaToken.IsCancellationRequested);
            Assert.False(betaToken.IsCancellationRequested);
            alpha.Lifetime.Cancel();
            Assert.True(alphaToken.IsCancellationRequested);
            Assert.False(betaToken.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => alphaOperation.WaitAsync(root.Lifetime.Token));
        }
        finally { releaseBeta.TrySetResult(); }
        Assert.Contains("BETA DONE", JsonSerializer.Serialize(await betaOperation));
        using var followup = Child(root, "alpha", "thread-alpha", ctx => Stream(ctx, Events(Terminal(Message("ALPHA FOLLOWUP")))));
        var next = ClientRequest();
        next["input"] = new List<object?> { new D { ["type"] = "agent_message", ["id"] = "alpha-restart", ["author"] = "/root", ["recipient"] = "/root/alpha", ["content"] = "retry alpha" } };
        Assert.Contains("ALPHA FOLLOWUP", JsonSerializer.Serialize(await Complete(followup, next)));
    }

    [Fact]
    public async Task RequestWithoutNativeIdentityOrToolsKeepsLegacyServerCoordination()
    {
        using var root = new MultiAgentApiTestContext(ctx => Stream(ctx, Events(Terminal(Message("LEGACY")))));
        var response = await Complete(root, Request());
        Assert.Contains("LEGACY", JsonSerializer.Serialize(response));
        Assert.Contains("ocxp_ma_spawn_agent", JsonSerializer.Serialize(root.FakeEndpoint.Calls.Single().Payload!["tools"]));
        Assert.Null(await root.ClientStore.TryGetAsync(root.Key, "root-thread", "root-thread", root.Lifetime.Token));
    }

    internal static MultiAgentApiTestContext Root(Func<ProxyEndpointContext, Task<ProxyEndpointResult>> handler)
    {
        var test = new MultiAgentApiTestContext(handler, session: "root-thread");
        Identify(test, "root-thread");
        return test;
    }

    internal static MultiAgentApiTestContext Child(MultiAgentApiTestContext root, string name, string thread,
        Func<ProxyEndpointContext, Task<ProxyEndpointResult>> handler)
    {
        var test = new MultiAgentApiTestContext(handler, root.Store, root.Key, "root-thread", root.ClientStore);
        Identify(test, thread, "root-thread", "/root/" + name);
        return test;
    }

    internal static void Identify(MultiAgentApiTestContext test, string thread, string parent = "", string agent = "/root")
    {
        test.Http.Request.Headers["thread-id"] = thread;
        var metadata = new D { ["thread_id"] = thread, ["agent_name"] = agent, ["turn_id"] = "turn-" + Guid.NewGuid().ToString("N") };
        if (parent.Length > 0)
        {
            test.Http.Request.Headers["x-codex-parent-thread-id"] = parent;
            metadata["parent_thread_id"] = parent;
            metadata["subagent_kind"] = "thread_spawn";
        }
        test.Http.Request.Headers["x-codex-turn-metadata"] = JsonSerializer.Serialize(metadata);
    }

    internal static D ClientRequest(string model = "fake", string message = "root task")
    {
        var request = Request();
        request["model"] = model;
        request["input"] = new List<object?> { new D { ["role"] = "user", ["content"] = message } };
        request["tools"] = new List<object?> { new D
        {
            ["type"] = "namespace", ["name"] = "collaboration", ["tools"] = new List<object?>
            {
                Function("spawn_agent", ["task_name", "message"], "task_name", "message", "fork_turns", "model", "reasoning_effort"),
                Function("followup_task", ["target", "message"], "target", "message"),
                Function("send_message", ["target", "message"], "target", "message"),
                Function("interrupt_agent", ["target"], "target"),
                Function("wait_agent", []), Function("list_agents", [])
            }
        } };
        return request;
    }

    private static D Function(string name, string[] required, params string[] properties) => new()
    {
        ["type"] = "function", ["name"] = name, ["parameters"] = new D
        {
            ["type"] = "object", ["properties"] = properties.ToDictionary(name => name, _ => (object?)new D { ["type"] = "string" }),
            ["required"] = required.ToList(), ["additionalProperties"] = false
        }
    };

    internal static D Spawn(string id, string name, string model, string message) => NativeCall(id, "spawn_agent", new D
    {
        ["task_name"] = name, ["message"] = message, ["model"] = model, ["fork_turns"] = "none"
    });

    internal static D NativeCall(string id, string name, D arguments) => new()
    {
        ["type"] = "function_call", ["id"] = "up_" + id, ["call_id"] = id, ["namespace"] = "collaboration", ["name"] = name,
        ["arguments"] = JsonSerializer.Serialize(arguments), ["status"] = "completed"
    };

    internal static D SpawnResult(D call, string name) => new()
    {
        ["type"] = "function_call_output", ["call_id"] = call["call_id"], ["output"] = JsonSerializer.Serialize(new D { ["task_name"] = name })
    };

    internal static D Continue(D previous, params D[] items)
    {
        var request = ClientRequest();
        request["previous_response_id"] = previous["id"];
        request["input"] = items.Cast<object?>().ToList();
        return request;
    }

    internal static List<D> Output(D response) => JsonDictionaryValue.List(response, "output").OfType<D>().ToList();

    internal static async Task<D> Complete(MultiAgentApiTestContext test, D request)
    {
        var response = Assert.IsType<ObjectResult>(await test.Service.Responses(request));
        Assert.Equal(200, response.StatusCode);
        return Assert.IsType<D>(response.Value);
    }
}

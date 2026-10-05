using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;
using static OpenCodex.Api.Tests.MultiAgentApiTestContext;
using static OpenCodex.Api.Tests.MultiAgentClientResponseTests;

namespace OpenCodex.Api.Tests;

public sealed class NativeClientApiTests
{
    [Fact]
    public async Task ForkHistoryIsAnAuthoritativeProjectionWithoutServerTaskInstructions()
    {
        using var root = Root(ctx => Stream(ctx, Events(Terminal(Message("NEW ANSWER")))));
        var request = ClientRequest();
        request["input"] = new List<object?>
        {
            User("old", "Already answered question"), Message("Old answer"), User("new", "Only answer the new question")
        };
        var expected = JsonSerializer.Serialize(request["input"]);
        await Complete(root, request);
        Assert.Equal(expected, JsonSerializer.Serialize(Assert.Single(root.FakeEndpoint.Calls).Payload!["input"]));
        Assert.Null(await root.ClientStore.TryGetAsync(root.Key, "root-thread", "root-thread", default));
    }

    [Fact]
    public async Task CompactionAndEnvironmentMessagesDoNotScheduleExtraTasks()
    {
        var calls = 0;
        using var root = Root(ctx => Stream(ctx, Events(Terminal(++calls == 1
            ? NativeCall("list", "list_agents", new D()) : Message("CURRENT ANSWER")))));
        var request = ClientRequest();
        request["input"] = new List<object?> { User("task", "Answer current question") };
        var first = await Complete(root, request);
        var next = ClientRequest();
        next["input"] = new List<object?>
        {
            User("task", "Answer current question"), Output(first).Single(),
            new D { ["type"] = "function_call_output", ["call_id"] = Output(first).Single()["call_id"], ["output"] = "[]" },
            User("env", "<environment_context>date changed</environment_context>"),
            User("rules", "# AGENTS.md instructions: reply in Chinese"),
            User("compact", "Another language model started: continue current question")
        };
        var expected = JsonSerializer.Serialize(next["input"]);
        var result = await Complete(root, next);
        Assert.Equal(2, calls);
        Assert.Single(Output(result));
        Assert.Equal(expected, JsonSerializer.Serialize(root.FakeEndpoint.Calls.Last().Payload!["input"]));
    }

    [Fact]
    public async Task FullHistoryRetryReplaysButSameTextInANewTurnRunsAgain()
    {
        var count = 0;
        using var root = Root(ctx =>
        {
            count++;
            return Stream(ctx, Events(Terminal(Message("ANSWER " + count))));
        });
        var request = ClientRequest();
        var first = await Complete(root, request);
        var replay = await Complete(root, request);
        Assert.Equal(first["id"], replay["id"]);
        Assert.Equal(1, count);
        Identify(root, "root-thread");
        await Complete(root, request);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ExplicitClientCheckpointDoesNotTriggerAnAdditionalServerSummary()
    {
        using var root = Root(ctx => Stream(ctx, Events(Terminal(Message("CHECKPOINT")))));
        var request = ClientRequest();
        request["context_management"] = new D { ["compact_threshold"] = 1 };
        await Complete(root, request);
        Identify(root, "root-thread");
        request["input"] = new List<object?> { User("checkpoint", "Create a handoff summary for the client.") };
        await Complete(root, request);
        Assert.Equal(2, root.FakeEndpoint.Calls.Count);
        Assert.DoesNotContain(root.FakeEndpoint.Calls, c => JsonSerializer.Serialize(c.Payload).Contains("Create a factual checkpoint"));
    }

    private static D User(string id, string text) => new()
    {
        ["type"] = "message", ["id"] = id, ["role"] = "user", ["content"] = text
    };
}

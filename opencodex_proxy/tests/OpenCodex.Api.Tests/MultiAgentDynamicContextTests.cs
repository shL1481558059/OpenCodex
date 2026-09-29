using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using static OpenCodex.Api.Tests.MultiAgentTestHarness;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentDynamicContextTests
{
    [Fact]
    public async Task ContinuationReplacesInstructionsAndToolsWhileAcceptingRevokedToolResult()
    {
        var run = Run();
        run.Template = MultiAgentProtocol.NormalizeRequest(new()
        {
            ["model"] = "test", ["instructions"] = "INITIAL_INSTRUCTION",
            ["tools"] = new List<object?> { Tool("old_read") }
        });
        await Execute(run, (payload, ct) =>
        {
            Assert.Equal("INITIAL_INSTRUCTION", payload["instructions"]);
            Assert.Contains("old_read", ToolNames(payload));
            return Task.FromResult(Response(Call("old_read", new { path = "file" })));
        });
        var pending = Assert.Single(run.PendingCalls).Key;
        await Execute(run, (payload, ct) =>
        {
            Assert.Equal("NEW_INSTRUCTION", payload["instructions"]);
            Assert.DoesNotContain("old_read", ToolNames(payload));
            Assert.Contains("new_write", ToolNames(payload));
            Assert.Contains("PREVIOUS_TOOL_RESULT", History(payload));
            return Task.FromResult(Response(Message("DONE")));
        }, new()
        {
            ["instructions"] = "NEW_INSTRUCTION", ["tools"] = new List<object?> { Tool("new_write") },
            ["input"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "function_call_output", ["call_id"] = pending, ["output"] = "PREVIOUS_TOOL_RESULT" } }
        });
        Assert.True(run.Finished);
        Assert.Contains(pending, run.ReceivedCalls);
        Assert.Empty(run.PendingCalls);
    }

    [Fact]
    public async Task CompletedRunNewTaskCanClearInstructionsAndClientTools()
    {
        var run = Run(); run.Template["instructions"] = "OLD";
        await Execute(run, (payload, ct) => Task.FromResult(Response(Message("FIRST"))));
        await Execute(run, (payload, ct) =>
        {
            Assert.False(payload.ContainsKey("instructions"));
            Assert.All(ToolNames(payload), name => Assert.NotNull(MultiAgentProtocol.ActionName(name)));
            Assert.Contains("SECOND_TASK", History(payload));
            return Task.FromResult(Response(Message("SECOND")));
        }, new() { ["instructions"] = null, ["tools"] = new List<object?>(), ["input"] = "SECOND_TASK" });
        Assert.True(run.Finished);
        Assert.Equal(2, run.ModelTurns);
    }

    [Fact]
    public async Task ContinuationAdditionalToolsAugmentInheritedSetAndParametersUpdate()
    {
        var run = Run(); run.Template["tools"] = JsonDictionaryValue.List(run.Template, "tools").Append(Tool("first")).ToList();
        run.Template["instructions"] = "RETAIN";
        await Execute(run, (payload, ct) => Task.FromResult(Response(Message("FIRST"))));
        await Execute(run, (payload, ct) =>
        {
            Assert.Equal("RETAIN", payload["instructions"]);
            Assert.Contains("first", ToolNames(payload)); Assert.Contains("second", ToolNames(payload));
            Assert.Equal("high", JsonDictionaryValue.String((Dictionary<string, object?>)payload["reasoning"]!, "effort"));
            return Task.FromResult(Response(Message("SECOND")));
        }, new()
        {
            ["reasoning"] = new Dictionary<string, object?> { ["effort"] = "high" },
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "additional_tools", ["tools"] = new List<object?> { Tool("second") } },
                Message("NEXT_TASK", "user")
            }
        });
        Assert.True(run.Finished);
    }

    [Fact]
    public async Task InputInstructionReplacementReachesExistingChildAndRootAfterToolPause()
    {
        var run = Run();
        run.Agents["/root"].History.Insert(0, Message("OLD_SYSTEM", "system"));
        run.Agents["/root"].History.Insert(1, Message("OLD_DEVELOPER", "developer"));
        run.Agents["/root/worker"] = new()
        {
            Name = "/root/worker", Parent = "/root", LastTaskMessage = "WORK",
            History = [Message("OLD_SYSTEM", "system"), Message("OLD_DEVELOPER", "developer"), Message("WORK", "user")]
        };
        await Execute(run, (payload, ct) => Task.FromResult(Response(Call("read", new { }))));
        Assert.Equal(2, run.PendingCalls.Count);
        var updatedAgents = new HashSet<string>();
        var incoming = run.PendingCalls.Select(pair => (object?)new Dictionary<string, object?>
        { ["type"] = "function_call_output", ["call_id"] = pair.Key, ["output"] = "READ_DONE" }).ToList();
        incoming.Add(Message("NEW_SYSTEM", "system"));
        incoming.Add(Message("NEW_DEVELOPER", "developer"));
        await Execute(run, (payload, ct) =>
        {
            var history = History(payload);
            Assert.Contains("NEW_SYSTEM", history); Assert.Contains("NEW_DEVELOPER", history);
            Assert.DoesNotContain("OLD_SYSTEM", history); Assert.DoesNotContain("OLD_DEVELOPER", history);
            Assert.Contains("READ_DONE", history);
            updatedAgents.Add(Agent(payload));
            return Task.FromResult(Response(Message("DONE")));
        }, new() { ["input"] = incoming });
        Assert.Contains("/root", updatedAgents); Assert.Contains("/root/worker", updatedAgents);
        Assert.True(run.Finished);
    }

    [Fact]
    public async Task InputInstructionReplacementIsInheritedByLaterRequestWithoutInstructions()
    {
        var run = Run();
        await Execute(run, (payload, ct) => Task.FromResult(Response(Message("FIRST"))),
            new() { ["input"] = new List<object?> { Message("LATEST_POLICY", "developer") } });
        await Execute(run, (payload, ct) =>
        {
            Assert.Contains("LATEST_POLICY", History(payload));
            Assert.Equal(1, JsonDictionaryValue.List(payload, "input").OfType<Dictionary<string, object?>>()
                .Count(item => System.Text.Json.JsonSerializer.Serialize(item).Contains("LATEST_POLICY")));
            return Task.FromResult(Response(Message("SECOND")));
        }, new() { ["input"] = "NEXT_REQUEST" });
        Assert.True(run.Finished);
    }

    [Fact]
    public async Task IdenticalTaskOnNextResponseStartsAgainButRetryOfSameContinuationDoesNot()
    {
        var run = Run(); var calls = 0;
        Task<Dictionary<string, object?>> Model(Dictionary<string, object?> payload, CancellationToken ct)
        { calls++; return Task.FromResult(Response(Message("DONE"))); }
        var first = await Execute(run, Model, new() { ["input"] = "REPEAT_TASK" });
        var firstResponse = JsonDictionaryValue.Object(Assert.Single(first, e => JsonDictionaryValue.String(e, "type") == "response.completed"), "response", WebSearchPayload.DeepCopyObject);
        var next = new Dictionary<string, object?> { ["input"] = "REPEAT_TASK", ["previous_response_id"] = firstResponse["id"] };
        await Execute(run, Model, next);
        Assert.Equal(2, calls);
        await Execute(run, Model, next);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ReplayedFullHistoryRecognizesNewOccurrenceOfIdenticalUserMessage()
    {
        var run = Run(); var calls = 0;
        Task<Dictionary<string, object?>> Model(Dictionary<string, object?> payload, CancellationToken ct)
        { calls++; return Task.FromResult(Response(Message("DONE"))); }
        await Execute(run, Model, new() { ["input"] = new List<object?> { Message("REPEAT_TASK", "user") } });
        var fullHistory = new Dictionary<string, object?>
        {
            ["input"] = new List<object?> { Message("REPEAT_TASK", "user"), Message("DONE"), Message("REPEAT_TASK", "user") }
        };
        await Execute(run, Model, fullHistory);
        Assert.Equal(2, calls);
        await Execute(run, Model, fullHistory);
        Assert.Equal(2, calls);
    }

    private static Dictionary<string, object?> Tool(string name) => new() { ["type"] = "function", ["name"] = name, ["parameters"] = new Dictionary<string, object?> { ["type"] = "object" } };
    private static IEnumerable<string> ToolNames(Dictionary<string, object?> payload) => JsonDictionaryValue.List(payload, "tools").OfType<Dictionary<string, object?>>().Select(tool => JsonDictionaryValue.String(tool, "name"));
}

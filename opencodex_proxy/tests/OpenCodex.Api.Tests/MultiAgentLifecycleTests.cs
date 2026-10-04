using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using static OpenCodex.Api.Tests.MultiAgentTestHarness;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentLifecycleTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t")]
    public async Task EmptyFinalMessageCannotFinishTaskAndRetryStillCallsModel(string? text)
    {
        var run = Run();
        var calls = 0;
        Task<Dictionary<string, object?>> Model(Dictionary<string, object?> _, CancellationToken ct)
        {
            var item = Message(++calls == 1 ? text ?? "" : "FINAL");
            if (calls == 1 && text is null) item["content"] = new List<object?>();
            return Task.FromResult(Response(item));
        }
        var first = await Execute(run, Model);
        Assert.Contains(first, e => JsonDictionaryValue.String(e, "type") == "response.failed");
        Assert.False(run.Finished);
        Assert.Empty(run.Agents["/root"].CompletedTurns);
        var second = await Execute(run, Model);
        Assert.Equal(2, calls);
        Assert.True(run.Finished);
        Assert.Contains("FINAL", System.Text.Json.JsonSerializer.Serialize(second));
    }

    [Theory]
    [InlineData("{\"query\":\"keep literal JSON\"}", false)]
    [InlineData("{\"query\":\"keep literal JSON\"}", true)]
    [InlineData("{\"input\":\"literal\"}", false)]
    [InlineData("{\"input\":\"literal\"}", true)]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("{}", false)]
    [InlineData("{}", true)]
    [InlineData("{\"input\":\"\\u4e2d\\u6587\\n\\\"quoted\\\"\\\\path\\ud83d\\ude00\"}", false)]
    [InlineData("{\"input\":\"\\u4e2d\\u6587\\n\\\"quoted\\\"\\\\path\\ud83d\\ude00\"}", true)]
    [InlineData("text(\"中文😀\\n\\\"quoted\\\"\\\\path\");\n", false)]
    [InlineData("text(\"中文😀\\n\\\"quoted\\\"\\\\path\");\n", true)]
    public async Task RawCustomToolJson_IsNotMistakenForChatInputEnvelope(string rawInput, bool streaming)
    {
        var run = Run();
        var events = new List<Dictionary<string, object?>>();
        MultiAgentModelCall model = async (_, emit, ct) =>
        {
            var item = new Dictionary<string, object?>
            {
                ["type"] = "custom_tool_call", ["id"] = "source-item", ["call_id"] = "source-call",
                ["name"] = "custom_json", ["input"] = ""
            };
            if (streaming)
            {
                await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = item }, ct);
                foreach (var fragment in rawInput.Select(character => character.ToString()))
                    await emit(new() { ["type"] = "response.custom_tool_call_input.delta", ["output_index"] = 0, ["delta"] = fragment }, ct);
                await emit(new() { ["type"] = "response.custom_tool_call_input.done", ["output_index"] = 0, ["input"] = rawInput }, ct);
            }
            item["input"] = rawInput;
            if (streaming)
                await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item }, ct);
            return Response(item);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await new MultiAgentRuntime(run, model, e => { events.Add(e); return Task.CompletedTask; }, () => Task.CompletedTask)
            .ExecuteAsync(new(), deadline.Token);
        var deltas = string.Concat(events.Where(e => JsonDictionaryValue.String(e, "type") == "response.custom_tool_call_input.delta")
            .Select(e => Assert.IsType<string>(e["delta"])));
        Assert.Equal(rawInput, deltas);
        var done = Assert.Single(events, e => JsonDictionaryValue.String(e, "type") == "response.custom_tool_call_input.done");
        Assert.Equal(rawInput, Assert.IsType<string>(done["input"]));
        var itemDone = JsonDictionaryValue.Object(
            Assert.Single(events, e => JsonDictionaryValue.String(e, "type") == "response.output_item.done"),
            "item", WebSearchPayload.DeepCopyObject);
        var pending = Assert.Single(run.PendingCalls);
        Assert.Equal("/root", pending.Value);
        Assert.Equal(pending.Key, JsonDictionaryValue.String(itemDone, "call_id"));
        Assert.Equal(rawInput, Assert.IsType<string>(itemDone["input"]));
        var responseItem = Assert.IsType<Dictionary<string, object?>>(Assert.Single(JsonDictionaryValue.List(response, "output")));
        Assert.Equal(rawInput, Assert.IsType<string>(responseItem["input"]));
        var historyItem = Assert.Single(run.Agents["/root"].History.OfType<Dictionary<string, object?>>(),
            i => JsonDictionaryValue.String(i, "type") == "custom_tool_call");
        Assert.Equal(rawInput, Assert.IsType<string>(historyItem["input"]));
        Assert.Equal(pending.Key, JsonDictionaryValue.String(historyItem, "call_id"));
        Assert.Equal(rawInput, Assert.IsType<string>(
            Assert.IsType<Dictionary<string, object?>>(Assert.Single(run.OutputHistory))["input"]));
    }

    [Fact]
    public async Task FunctionArguments_AreForwardedWithoutTrimming()
    {
        const string arguments = " \n{\"value\":\"literal\"}\n\t";
        var run = Run();
        var events = await Execute(run, (_, _) => Task.FromResult(Response(new Dictionary<string, object?>
        {
            ["type"] = "function_call", ["call_id"] = "source-call", ["name"] = "local_read",
            ["arguments"] = arguments
        })));

        var delta = Assert.Single(events, e => JsonDictionaryValue.String(e, "type") == "response.function_call_arguments.delta");
        var done = Assert.Single(events, e => JsonDictionaryValue.String(e, "type") == "response.function_call_arguments.done");
        var item = JsonDictionaryValue.Object(
            Assert.Single(events, e => JsonDictionaryValue.String(e, "type") == "response.output_item.done"),
            "item", WebSearchPayload.DeepCopyObject);
        Assert.Equal(arguments, Assert.IsType<string>(delta["delta"]));
        Assert.Equal(arguments, Assert.IsType<string>(done["arguments"]));
        Assert.Equal(arguments, Assert.IsType<string>(item["arguments"]));
        var historyItem = Assert.Single(run.Agents["/root"].History.OfType<Dictionary<string, object?>>(),
            i => JsonDictionaryValue.String(i, "type") == "function_call");
        Assert.Equal(arguments, Assert.IsType<string>(historyItem["arguments"]));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("incomplete")]
    public async Task RootModelAttemptError_PreservesItsTaskForRetry(string status)
    {
        var run = Run();
        var events = await Execute(run, (payload, ct) => Task.FromResult(new Dictionary<string, object?>
        {
            ["status"] = status,
            ["output"] = new List<object?>(),
            ["error"] = new Dictionary<string, object?> { ["message"] = "root failed" },
            ["incomplete_details"] = new Dictionary<string, object?> { ["reason"] = "max_output_tokens" }
        }));
        Assert.Contains(events, e => JsonDictionaryValue.String(e, "type") == "response." + status);
        Assert.Equal("retryable", run.Agents["/root"].Status);
        Assert.True(run.Agents["/root"].TaskStarted);
        Assert.Empty(run.Agents["/root"].CompletedTurns);
        Assert.False(run.Finished);
    }

    [Fact]
    public async Task RetryOfCommittedFinalReplaysAnswerWithoutCallingModelOrTools()
    {
        var run = Run();
        var calls = 0;
        Task<Dictionary<string, object?>> Model(Dictionary<string, object?> _, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(Response(Message("COMMITTED_FINAL")));
        }
        await Execute(run, Model);
        var events = await Execute(run, Model);
        Assert.Equal(1, calls);
        Assert.Contains("COMMITTED_FINAL", System.Text.Json.JsonSerializer.Serialize(events));
        Assert.Single(run.Agents["/root"].CompletedTurns);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("incomplete")]
    [InlineData("interrupted")]
    public async Task TerminalTaskWithoutRunnableWorkCannotBecomeEmptySuccess(string status)
    {
        var run = Run();
        run.Agents["/root"].Status = status;
        var events = await Execute(run, (_, _) => throw new InvalidOperationException("Must not call the model"));
        Assert.Contains(events, e => JsonDictionaryValue.String(e, "type") == "response.failed");
        Assert.DoesNotContain(events, e => JsonDictionaryValue.String(e, "type") == "response.completed");
        Assert.False(run.Finished);
    }

    [Fact]
    public async Task Child_failure_is_reported_to_parent_without_cancelling_sibling()
    {
        var run = Run();
        var counts = new Dictionary<string, int>();
        var siblingCompleted = false;
        var events = await Execute(run, (payload, ct) =>
        {
            var name = Agent(payload); counts[name] = counts.GetValueOrDefault(name) + 1;
            if (name == "/root/a") throw new UpstreamException("child-a-failed", 502);
            if (name == "/root/b") { siblingCompleted = true; return Task.FromResult(Response(Message("CHILD_B_OK"))); }
            if (counts[name] == 1) return Task.FromResult(Response(
                Call("ocxp_ma_spawn_agent", new { task_name = "a", message = "A", fork_turns = "none" }),
                Call("ocxp_ma_spawn_agent", new { task_name = "b", message = "B", fork_turns = "none" })));
            return Task.FromResult(History(payload).Contains("child-a-failed") && History(payload).Contains("CHILD_B_OK")
                ? Response(Message("RECOVERED")) : Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 100 })));
        });
        Assert.True(siblingCompleted);
        Assert.Equal("failed", run.Agents["/root/a"].Status);
        Assert.True(run.Finished);
        Assert.Single(events.Where(e => JsonDictionaryValue.String(e, "type") == "response.completed"));
    }

    [Fact]
    public async Task Root_early_final_waits_for_child_report_and_synthesizes_again()
    {
        var run = Run();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootCalls = 0;
        var events = await Execute(run, async (payload, ct) =>
        {
            if (Agent(payload) != "/root") { await release.Task.WaitAsync(ct); return Response(Message("CHILD_RESULT")); }
            rootCalls++;
            if (rootCalls == 1) return Response(Call("ocxp_ma_spawn_agent", new { task_name = "a", message = "A" }));
            if (rootCalls == 2) { release.SetResult(); return Response(Message("PREMATURE")); }
            Assert.Contains("CHILD_RESULT", History(payload));
            return Response(Message("COMBINED"));
        });
        Assert.Equal(3, rootCalls);
        var finals = events.Where(e => JsonDictionaryValue.String(e, "type") == "response.output_item.done")
            .Select(e => JsonDictionaryValue.Object(e, "item", WebSearchPayload.DeepCopyObject))
            .Where(i => JsonDictionaryValue.String(i, "phase") == "final_answer"
                && JsonDictionaryValue.String(JsonDictionaryValue.Object(i, "agent", WebSearchPayload.DeepCopyObject), "agent_name") == "/root");
        Assert.Single(finals);
        Assert.Contains("COMBINED", System.Text.Json.JsonSerializer.Serialize(finals.Single()));
        Assert.True(run.Finished);
    }

    [Fact]
    public async Task Http_tool_pause_does_not_finish_run_and_duplicate_results_are_not_reapplied()
    {
        var run = Run(); var calls = 0;
        Task<Dictionary<string, object?>> Model(Dictionary<string, object?> p, CancellationToken ct) =>
            Task.FromResult(++calls == 1 ? Response(Call("local_read", new { })) : Response(Message("DONE")));
        await Execute(run, Model);
        Assert.False(run.Finished);
        var id = Assert.Single(run.PendingCalls).Key;
        var output = new Dictionary<string, object?> { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = "VALUE" };
        await Execute(run, Model, new() { ["input"] = new List<object?> { output, output } });
        Assert.True(run.Finished);
        Assert.Single(run.ReceivedCalls);
        Assert.Single(run.Agents["/root"].History.OfType<Dictionary<string, object?>>()
            .Where(i => JsonDictionaryValue.String(i, "type") == "function_call_output"));
    }
}

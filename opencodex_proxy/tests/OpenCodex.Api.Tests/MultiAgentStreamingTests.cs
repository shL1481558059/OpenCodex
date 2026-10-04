using System.Collections.Concurrent;
using System.Text.Json;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommentaryOnlyResponseContinuesSameTaskAndPreservesPhase(bool stream)
    {
        var run = Run();
        var events = new ConcurrentQueue<D>();
        var calls = 0;
        MultiAgentModelCall model = async (payload, emit, ct) =>
        {
            var first = ++calls == 1;
            if (!first) Assert.Contains("PROGRESS", JsonSerializer.Serialize(payload["input"]));
            var item = Message(first ? "PROGRESS" : "FINAL");
            item["phase"] = first ? "commentary" : "final_answer";
            if (stream)
            {
                await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = item }, ct);
                await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item }, ct);
            }
            return Response(item);
        };
        using var timeout = Deadline();
        await Runtime(run, model, events).ExecuteAsync(Request(), timeout.Token);
        Assert.Equal(2, calls);
        var done = events.Where(e => Type(e) == "response.output_item.done").Select(e => (D)e["item"]!).ToArray();
        Assert.Equal(new[] { "commentary", "final_answer" }, done.Select(i => (string)i["phase"]!));
        Assert.Equal("commentary", ((D)events.First(e => Type(e) == "response.output_item.added")["item"]!)["phase"]);
        Assert.Single(run.Agents["/root"].CompletedTurns);
        Assert.True(run.Finished);
    }

    [Fact]
    public async Task CommentaryAndFinalInOneResponseKeepTheirDistinctPhases()
    {
        var run = Run();
        var events = new ConcurrentQueue<D>();
        var commentary = Message("PROGRESS"); commentary["phase"] = "commentary";
        var final = Message("FINAL"); final["phase"] = "final_answer";
        using var timeout = Deadline();
        await Runtime(run, (_, _, _) => Task.FromResult(Response(commentary, final)), events)
            .ExecuteAsync(Request(), timeout.Token);
        var done = events.Where(e => Type(e) == "response.output_item.done").Select(e => (D)e["item"]!).ToArray();
        Assert.Equal(new[] { "commentary", "final_answer" }, done.Select(i => (string)i["phase"]!));
    }

    [Theory]
    [InlineData("text(1+1);")]
    [InlineData("{\"input\":\"literal\"}")]
    [InlineData(" \ntext(\"中文 😀 \\\"quoted\\\"\");\n ")]
    public async Task ConvertedCustomToolSurvivesClientExecutionAndSecondModelTurn(string rawInput)
    {
        await AssertConvertedCustomRoundTrip(new Dictionary<string, string>
        {
            ["/root"] = rawInput
        });
    }

    [Fact]
    public async Task ConcurrentConvertedCustomToolsKeepHistoryAndResultsWithTheirOwningAgent()
    {
        await AssertConvertedCustomRoundTrip(new Dictionary<string, string>
        {
            ["/root"] = "text('root');",
            ["/root/a"] = "{\"input\":\"literal\"}",
            ["/root/b"] = " \ntext(\"中文 😀\");\n "
        });
    }

    private static async Task AssertConvertedCustomRoundTrip(IReadOnlyDictionary<string, string> inputs)
    {
        var request = Request();
        request["tools"] = new List<object?> { new D { ["type"] = "custom", ["name"] = "exec" } };
        var run = Run();
        run.Template = MultiAgentProtocol.NormalizeRequest(request);
        foreach (var name in inputs.Keys.Where(name => name != "/root"))
            run.Agents[name] = new() { Name = name, Parent = "/root", Generation = 1 };
        var started = 0;
        var allStarted = NewSignal();
        var turns = new ConcurrentDictionary<string, int>();
        var nextRequests = new ConcurrentDictionary<string, D>();
        var events = new ConcurrentQueue<D>();
        MultiAgentModelCall model = async (payload, emit, ct) =>
        {
            var agent = Agent(payload);
            var chatRequest = ProtocolConverter.ConvertRequest(payload, ProtocolConverter.Responses,
                ProtocolConverter.Chat, "upstream");
            if (turns.AddOrUpdate(agent, 1, (_, current) => current + 1) == 1)
            {
                if (Interlocked.Increment(ref started) == inputs.Count) allStarted.TrySetResult();
                await allStarted.Task.WaitAsync(ct);
                return await StreamConvertedCustom(payload, inputs[agent], emit, ct);
            }
            nextRequests[agent] = chatRequest;
            return Response(Message(agent + " finished"));
        };
        using var timeout = Deadline();
        var firstResponse = await Runtime(run, model, events).ExecuteAsync(request, timeout.Token);
        var calls = JsonDictionaryValue.List(firstResponse, "output").OfType<D>()
            .Where(item => Type(item) == "custom_tool_call").ToArray();
        Assert.Equal(inputs.Count, calls.Length);
        Assert.Equal(inputs.Count, calls.Select(call => call["call_id"]).Distinct().Count());
        Assert.Equal(inputs.Count, calls.Select(call => call["id"]).Distinct().Count());
        var owners = new Dictionary<string, string>(run.PendingCalls);
        Assert.Equal(inputs.Count, owners.Count);
        foreach (var call in calls)
        {
            var agent = owners[(string)call["call_id"]!];
            Assert.Equal(inputs[agent], call["input"]);
            Assert.Equal(agent, EventAgent(call));
            var itemId = (string)call["id"]!;
            var deltas = events.Where(e => Type(e) == "response.custom_tool_call_input.delta"
                && Equals(e["item_id"], itemId));
            Assert.Equal(inputs[agent], string.Concat(deltas.Select(e => e["delta"])));
            var done = Assert.Single(events, e => Type(e) == "response.custom_tool_call_input.done"
                && Equals(e["item_id"], itemId));
            Assert.Equal(inputs[agent], done["input"]);
        }

        var continuation = new D
        {
            ["input"] = calls.Reverse().Select(call => (object?)new D
            {
                ["type"] = "custom_tool_call_output", ["call_id"] = call["call_id"],
                ["output"] = "RESULT:" + owners[(string)call["call_id"]!]
            }).ToList()
        };
        await Runtime(run, model, new ConcurrentQueue<D>()).ExecuteAsync(continuation, timeout.Token);
        Assert.Empty(run.PendingCalls);
        Assert.True(run.Finished);
        Assert.Equal(inputs.Count, nextRequests.Count);
        foreach (var (agent, chatRequest) in nextRequests)
        {
            var messages = JsonDictionaryValue.List(chatRequest, "messages").OfType<D>().ToArray();
            var historyCall = Assert.Single(messages.SelectMany(message =>
                JsonDictionaryValue.List(message, "tool_calls").OfType<D>()));
            var function = Assert.IsType<D>(historyCall["function"]);
            Assert.Equal("exec", function["name"]);
            using var arguments = JsonDocument.Parse(Assert.IsType<string>(function["arguments"]));
            Assert.Equal(inputs[agent], arguments.RootElement.GetProperty("input").GetString());
            Assert.Single(arguments.RootElement.EnumerateObject());
            var toolResult = Assert.Single(messages, message => JsonDictionaryValue.String(message, "role") == "tool");
            Assert.Equal(historyCall["id"], toolResult["tool_call_id"]);
            Assert.Equal(agent, owners[Assert.IsType<string>(toolResult["tool_call_id"])]);
            Assert.Equal("RESULT:" + agent, toolResult["content"]);
        }
        if (inputs.Count == 1) Assert.Equal(2, turns["/root"]);
    }

    private static async Task<D> StreamConvertedCustom(D payload, string rawInput,
        Func<D, CancellationToken, Task> emit, CancellationToken ct)
    {
        var result = new ConvertedStreamResult
        {
            ToolCallMappings = ProtocolConverter.BuildResponsesToolCallMappings(payload)
        };
        var converted = SseStreamConverter.ChatToResponsesEvents(CustomChatLines(rawInput), "fake", result, ct);
        D? response = null;
        await foreach (var e in SseStreamConverter.ParseEvents(SplitEventBlocks(converted), ct))
        {
            var data = Assert.IsType<D>(e.Data);
            await emit(data, ct);
            if (e.EventName == "response.completed") response = Assert.IsType<D>(data["response"]);
        }
        return Assert.IsType<D>(response);
    }

    private static async IAsyncEnumerable<string> SplitEventBlocks(IAsyncEnumerable<string> blocks)
    {
        await foreach (var block in blocks)
            foreach (var line in block.Split('\n'))
                yield return line;
    }

    private static async IAsyncEnumerable<string> CustomChatLines(string rawInput)
    {
        var arguments = JsonSerializer.Serialize(new D { ["input"] = rawInput });
        for (var offset = 0; offset < arguments.Length; offset += 3)
        {
            var function = new D { ["arguments"] = arguments.Substring(offset, Math.Min(3, arguments.Length - offset)) };
            var call = new D { ["index"] = 0, ["function"] = function };
            if (offset == 0)
            {
                function["name"] = "exec";
                call["id"] = "same_upstream_call";
                call["type"] = "function";
            }
            yield return "data: " + JsonSerializer.Serialize(new D
            {
                ["id"] = "same_upstream_response", ["model"] = "upstream",
                ["choices"] = new[] { new D { ["index"] = 0, ["delta"] = new D { ["tool_calls"] = new[] { call } } } }
            });
            yield return "";
        }
        yield return "data: {\"id\":\"same_upstream_response\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}";
        yield return "";
        yield return "data: [DONE]";
        yield return "";
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TextDeltaIsObservableBeforeModelCompletionAndIsNotReplayed()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var deltaArrived = NewSignal();
        var events = new ConcurrentQueue<D>();
        var run = Run();
        MultiAgentModelCall model = async (_, emit, ct) =>
        {
            await StartMessage(emit, "hello", ct);
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return await FinishMessage(emit, "hello", ct);
        };
        using var timeout = Deadline();
        var operation = Runtime(run, model, events, e => { if (Type(e) == "response.output_text.delta") deltaArrived.TrySetResult(); }).ExecuteAsync(Request(), timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        await deltaArrived.Task.WaitAsync(timeout.Token);
        Assert.False(operation.IsCompleted);
        Assert.DoesNotContain(events, e => Type(e) == "response.completed");
        release.SetResult();
        var response = await operation;
        Assert.Equal("completed", response["status"]);
        Assert.Single(events, e => Type(e) == "response.output_text.delta");
        Assert.Single(events, e => Type(e) == "response.completed");
        Assert.Contains("hello", JsonSerializer.Serialize(response["output"]));
        AssertOrdered(events);
    }

    [Fact]
    public async Task IdenticalUpstreamIdsAndIndexesRemainDistinctAcrossConcurrentAgents()
    {
        var a = NewSignal();
        var b = NewSignal();
        var release = NewSignal();
        var bothDeltas = NewSignal();
        var events = new ConcurrentQueue<D>();
        var run = Run();
        run.Agents["/root/a"] = new() { Name = "/root/a", Parent = "/root", Generation = 1 };
        run.Agents["/root/b"] = new() { Name = "/root/b", Parent = "/root", Generation = 1 };
        var rootCalls = 0;
        MultiAgentModelCall model = async (payload, emit, ct) =>
        {
            var agent = Agent(payload);
            if (agent == "/root")
                return Interlocked.Increment(ref rootCalls) == 1
                    ? Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 60000 })) : Response(Message("ROOT_DONE"));
            var text = agent.EndsWith("/a") ? "ALPHA" : "BETA";
            await StartMessage(emit, text, ct);
            (agent.EndsWith("/a") ? a : b).SetResult();
            await release.Task.WaitAsync(ct);
            return await FinishMessage(emit, text, ct);
        };
        using var timeout = Deadline();
        var operation = Runtime(run, model, events, _ =>
        {
            if (events.Count(e => Type(e) == "response.output_text.delta") == 2) bothDeltas.TrySetResult();
        }).ExecuteAsync(Request(), timeout.Token);
        await Task.WhenAll(a.Task, b.Task).WaitAsync(timeout.Token);
        await bothDeltas.Task.WaitAsync(timeout.Token);
        var deltas = events.Where(e => Type(e) == "response.output_text.delta").ToArray();
        Assert.Equal(2, deltas.Select(e => e["item_id"]).Distinct().Count());
        Assert.Equal(2, deltas.Select(e => e["output_index"]).Distinct().Count());
        Assert.Equal("ALPHA", Assert.Single(deltas, e => EventAgent(e) == "/root/a")["delta"]);
        Assert.Equal("BETA", Assert.Single(deltas, e => EventAgent(e) == "/root/b")["delta"]);
        release.SetResult();
        await operation;
        Assert.Single(events, e => Type(e) == "response.completed");
        Assert.Single(events, e => Type(e) == "response.output_text.delta" && EventAgent(e) == "/root/a");
        Assert.Single(events, e => Type(e) == "response.output_text.delta" && EventAgent(e) == "/root/b");
        AssertOrdered(events);
    }

    [Theory]
    [InlineData("function_call", "local_read", "arguments", "{\"path\":\"a\"}")]
    [InlineData("custom_tool_call", "apply_patch", "input", "*** Begin Patch\n*** End Patch")]
    public async Task ClientToolDonePublishesCompleteArgumentsAndRegistersPendingCall(string kind, string name, string field, string value)
    {
        var staged = NewSignal();
        var release = NewSignal();
        var events = new ConcurrentQueue<D>();
        var run = Run();
        var pendingAtDone = false;
        MultiAgentModelCall model = async (_, emit, ct) =>
        {
            var item = new D { ["type"] = kind, ["id"] = "same_item", ["call_id"] = "same_call", ["name"] = name, [field] = "" };
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = item }, ct);
            await emit(new() { ["type"] = $"response.{kind}_{field}.delta", ["output_index"] = 0, ["item_id"] = "same_item", ["delta"] = value[..3] }, ct);
            staged.SetResult();
            await release.Task.WaitAsync(ct);
            await emit(new() { ["type"] = $"response.{kind}_{field}.delta", ["output_index"] = 0, ["item_id"] = "same_item", ["delta"] = value[3..] }, ct);
            item[field] = value;
            await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item }, ct);
            return Response(item);
        };
        using var timeout = Deadline();
        var runtime = new MultiAgentRuntime(run, model, e =>
        {
            events.Enqueue(e);
            if (Type(e) == "response.output_item.done" && Type((D)e["item"]!) == kind)
                pendingAtDone = run.PendingCalls.ContainsKey((string)((D)e["item"]!)["call_id"]!);
            return Task.CompletedTask;
        }, () => Task.CompletedTask);
        var operation = runtime.ExecuteAsync(Request(), timeout.Token);
        await staged.Task.WaitAsync(timeout.Token);
        Assert.Empty(run.PendingCalls);
        Assert.DoesNotContain(events, e => Type(e) == "response.output_item.done" && Type((D)e["item"]!) == kind);
        release.SetResult();
        await operation;
        var done = Assert.Single(events, e => Type(e) == "response.output_item.done" && Type((D)e["item"]!) == kind);
        var final = (D)done["item"]!;
        Assert.Equal(value, final[field]);
        Assert.NotEqual("same_call", final["call_id"]);
        Assert.True(pendingAtDone);
        Assert.Equal("/root", run.PendingCalls[(string)final["call_id"]!]);
        Assert.False(run.Finished);
    }

    [Fact]
    public async Task InternalActionNeverLeaksAsAClientFunctionCall()
    {
        var events = new ConcurrentQueue<D>();
        var calls = 0;
        MultiAgentModelCall model = async (_, emit, ct) =>
        {
            if (++calls > 1) return Response(Message("DONE"));
            var item = Call("ocxp_ma_list_agents", new { });
            item["id"] = "same_item";
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = item }, ct);
            await emit(new() { ["type"] = "response.function_call_arguments.delta", ["output_index"] = 0, ["item_id"] = "same_item", ["delta"] = "{}" }, ct);
            await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item }, ct);
            return Response(item);
        };
        using var timeout = Deadline();
        await Runtime(Run(), model, events).ExecuteAsync(Request(), timeout.Token);
        Assert.DoesNotContain(events, e => Type(e).StartsWith("response.function_call_", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.TryGetValue("item", out var item) && Type((D)item!) == "function_call");
        Assert.Contains(events, e => e.TryGetValue("item", out var item) && Type((D)item!) == "multi_agent_call");
    }

    [Fact]
    public async Task InterruptedGenerationCannotPublishLateDelta()
    {
        var childStarted = NewSignal();
        var cancelled = NewSignal();
        var lateAttempted = NewSignal();
        var events = new ConcurrentQueue<D>();
        var run = Run();
        run.Agents["/root/a"] = new() { Name = "/root/a", Parent = "/root", Generation = 1 };
        var roots = 0;
        var children = 0;
        MultiAgentModelCall model = async (payload, emit, ct) =>
        {
            if (Agent(payload) == "/root")
            {
                if (++roots == 1)
                {
                    await childStarted.Task.WaitAsync(ct);
                    return Response(Call("ocxp_ma_interrupt_agent", new { target = "a" }), Call("ocxp_ma_followup_task", new { target = "a", message = "NEW" }));
                }
                if (roots == 2) return Response(Call("ocxp_ma_wait_agent", new { timeout_ms = 60000 }));
                return Response(Message("ROOT_DONE"));
            }
            if (++children > 1) return await StreamMessage(emit, "NEW_GENERATION", ct);
            using var registration = ct.Register(() => cancelled.TrySetResult());
            await StartMessage(emit, "OLD_FIRST", ct);
            childStarted.SetResult();
            await cancelled.Task;
            await emit(new() { ["type"] = "response.output_text.delta", ["item_id"] = "same_item", ["output_index"] = 0, ["content_index"] = 0, ["delta"] = "STALE_LATE" }, CancellationToken.None);
            lateAttempted.SetResult();
            return Response(Message("OLD_FINAL"));
        };
        using var timeout = Deadline();
        await Runtime(run, model, events).ExecuteAsync(Request(), timeout.Token);
        Assert.True(lateAttempted.Task.IsCompletedSuccessfully);
        Assert.DoesNotContain(events, e => e.TryGetValue("delta", out var delta) && Equals(delta, "STALE_LATE"));
        Assert.Contains(events, e => e.TryGetValue("delta", out var delta) && Equals(delta, "NEW_GENERATION"));
        Assert.Single(events, e => Type(e) == "response.completed");
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("incomplete")]
    public async Task UnsuccessfulRootDoesNotPublishSuccessfulCompletion(string status)
    {
        var events = new ConcurrentQueue<D>();
        MultiAgentModelCall model = async (_, emit, ct) =>
        {
            await StartMessage(emit, "partial", ct);
            var response = Response(Message("partial"));
            response["status"] = status;
            response["error"] = new D { ["code"] = "model_error", ["message"] = "failed" };
            response["incomplete_details"] = new D { ["reason"] = "max_output_tokens" };
            await emit(new() { ["type"] = "response." + status, ["response"] = response }, ct);
            return response;
        };
        using var timeout = Deadline();
        var response = await Runtime(Run(), model, events).ExecuteAsync(Request(), timeout.Token);
        Assert.Equal(status, response["status"]);
        Assert.DoesNotContain(events, e => Type(e) == "response.completed");
        Assert.Single(events, e => Type(e) == "response." + status);
        var closed = Assert.Single(events, e => Type(e) == "response.output_item.done" && Type((D)e["item"]!) == "message");
        Assert.Equal("incomplete", ((D)closed["item"]!)["status"]);
        Assert.Equal("response." + status, Type(events.Last()));
    }

    [Theory]
    [InlineData("function_call", "arguments", "failed")]
    [InlineData("function_call", "arguments", "incomplete")]
    [InlineData("custom_tool_call", "input", "failed")]
    [InlineData("custom_tool_call", "input", "incomplete")]
    public async Task IncompleteToolArgumentsAreNeverPublishedAsExecutable(string kind, string field, string status)
    {
        var events = new ConcurrentQueue<D>();
        var run = Run();
        MultiAgentModelCall model = async (_, emit, ct) =>
        {
            var item = new D { ["type"] = kind, ["name"] = "local_tool", ["id"] = "same_item", ["call_id"] = "same_call", [field] = "" };
            await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = item }, ct);
            await emit(new() { ["type"] = $"response.{kind}_{field}.delta", ["item_id"] = "same_item", ["output_index"] = 0, ["delta"] = "unfinished" }, ct);
            var response = Response();
            response["status"] = status;
            response["incomplete_details"] = new D { ["reason"] = "max_output_tokens" };
            await emit(new() { ["type"] = "response." + status, ["response"] = response }, ct);
            return response;
        };
        using var timeout = Deadline();
        var result = await Runtime(run, model, events).ExecuteAsync(Request(), timeout.Token);
        Assert.Equal(status, result["status"]);
        Assert.Empty(run.PendingCalls);
        Assert.DoesNotContain(events, e => Type(e) == "response.output_item.done" && Type((D)e["item"]!) == kind);
        Assert.DoesNotContain(events, e => Type(e) == $"response.{kind}_{field}.done");
        Assert.Equal("response." + status, Type(events.Last()));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(10));
    private static string Type(D item) => JsonDictionaryValue.String(item, "type");
    private static string EventAgent(D item) => JsonDictionaryValue.String((D)item["agent"]!, "agent_name");
    private static string Agent(D payload) => ((string)payload["prompt_cache_key"]!).Split(':')[1];
    private static D Request() => new() { ["model"] = "fake", ["store"] = false, ["input"] = new List<object?> { new D { ["role"] = "user", ["content"] = "work" } } };
    private static MultiAgentRun Run() => new()
    {
        Model = "fake", Template = MultiAgentProtocol.NormalizeRequest(Request()),
        Agents = new() { ["/root"] = new() { Name = "/root", History = MultiAgentProtocol.InitialHistory(Request()) } }
    };
    private static MultiAgentRuntime Runtime(MultiAgentRun run, MultiAgentModelCall model, ConcurrentQueue<D> events, Action<D>? observed = null) =>
        new(run, model, e => { events.Enqueue(e); observed?.Invoke(e); return Task.CompletedTask; }, () => Task.CompletedTask);
    private static D Response(params D[] items) => new()
    {
        ["status"] = "completed", ["output"] = items.Cast<object?>().ToList(),
        ["usage"] = new D { ["input_tokens"] = 1, ["output_tokens"] = 1 }
    };
    private static D Message(string text) => new()
    {
        ["type"] = "message", ["id"] = "same_item", ["role"] = "assistant", ["status"] = "completed",
        ["content"] = new List<object?> { new D { ["type"] = "output_text", ["text"] = text } }
    };
    private static D Call(string name, object arguments) => new()
    {
        ["type"] = "function_call", ["name"] = name, ["call_id"] = Guid.NewGuid().ToString(), ["arguments"] = JsonSerializer.Serialize(arguments)
    };
    private static async Task StartMessage(Func<D, CancellationToken, Task> emit, string text, CancellationToken ct)
    {
        var item = Message("");
        item["status"] = "in_progress";
        await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = item }, ct);
        await emit(new() { ["type"] = "response.content_part.added", ["item_id"] = "same_item", ["output_index"] = 0, ["content_index"] = 0, ["part"] = new D { ["type"] = "output_text", ["text"] = "" } }, ct);
        await emit(new() { ["type"] = "response.output_text.delta", ["item_id"] = "same_item", ["output_index"] = 0, ["content_index"] = 0, ["delta"] = text }, ct);
    }
    private static async Task<D> FinishMessage(Func<D, CancellationToken, Task> emit, string text, CancellationToken ct)
    {
        var item = Message(text);
        await emit(new() { ["type"] = "response.output_text.done", ["item_id"] = "same_item", ["output_index"] = 0, ["content_index"] = 0, ["text"] = text }, ct);
        await emit(new() { ["type"] = "response.content_part.done", ["item_id"] = "same_item", ["output_index"] = 0, ["content_index"] = 0, ["part"] = ((List<object?>)item["content"]!)[0] }, ct);
        await emit(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item }, ct);
        var response = Response(item);
        await emit(new() { ["type"] = "response.completed", ["response"] = response }, ct);
        return response;
    }
    private static async Task<D> StreamMessage(Func<D, CancellationToken, Task> emit, string text, CancellationToken ct)
    {
        await StartMessage(emit, text, ct);
        return await FinishMessage(emit, text, ct);
    }
    private static void AssertOrdered(IEnumerable<D> events)
    {
        var sequences = events.Select(e => Convert.ToInt64(e["sequence_number"])).ToArray();
        Assert.True(sequences.Zip(sequences.Skip(1)).All(pair => pair.First < pair.Second));
    }
}

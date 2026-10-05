using OpenCodex.Core.Errors;
using System.Threading.Channels;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentClientRuntimeTests
{
    [Theory]
    [InlineData("exec", null, "function_call")]
    [InlineData("exec", "functions", "function_call")]
    [InlineData("exec", "other", "custom_tool_call")]
    public async Task InvalidOrdinaryClientCallRejectsWholeBatchBeforeAnyPreparation(string name, string? ns, string type)
    {
        var run = NativeRun();
        run.Template = NativeTools().ApplyToTemplate(run.Template);
        var definitions = JsonDictionaryValue.List(run.Template, "tools");
        definitions.Add(new D { ["type"] = "namespace", ["name"] = "functions", ["tools"] = new List<object?>
        {
            new D { ["type"] = "custom", ["name"] = "exec" }
        } });
        definitions.Add(new D { ["type"] = "namespace", ["name"] = "other", ["tools"] = new List<object?>
        {
            new D { ["type"] = "function", ["name"] = "exec", ["parameters"] = new D { ["type"] = "object" } }
        } });
        var invalid = new D
        {
            ["type"] = type, ["call_id"] = "bad-call", ["name"] = name,
            [type == "custom_tool_call" ? "input" : "arguments"] = "{}"
        };
        if (ns is not null) invalid["namespace"] = ns;
        var events = new List<D>();
        var prepared = 0;
        var runtime = new MultiAgentRuntime(run, (_, _) => Task.FromResult(MultiAgentTestHarness.Response(NativeCall(), invalid)),
            e => { events.Add(e); return Task.CompletedTask; }, () => Task.CompletedTask, client: new()
            {
                Tools = NativeTools(), BeforeToolCall = (_, _, _, _) => { prepared++; return Task.CompletedTask; }
            });
        var response = await runtime.ExecuteAsync(new(), default);
        Assert.Equal("failed", response["status"]);
        Assert.Equal(0, prepared);
        Assert.Empty(run.PendingCalls);
        Assert.DoesNotContain(run.Agents["/root"].History.OfType<D>(), item =>
            JsonDictionaryValue.String(item, "type") is "function_call" or "custom_tool_call");
        Assert.DoesNotContain(events, e => JsonDictionaryValue.String(e, "type") is
            "response.function_call_arguments.done" or "response.custom_tool_call_input.done" or "response.output_item.done");
    }

    [Fact]
    public async Task NewInputDuringRetryKeepsCurrentGenerationAndQueuesFollowupOnlyOnce()
    {
        var run = NativeRun();
        var actor = run.Agents["/root"];
        actor.Generation = actor.CurrentTaskGeneration = 7;
        var calls = 0;
        Task<D> Model(D payload, CancellationToken ct)
        {
            calls++;
            if (calls <= 2) throw new UpstreamException("temporary model failure");
            if (calls == 3) Assert.DoesNotContain("FOLLOWUP", WebSearchPayload.JsonDumps(payload["input"]));
            else Assert.Contains("FOLLOWUP", WebSearchPayload.JsonDumps(payload["input"]));
            return Task.FromResult(MultiAgentTestHarness.Response(MultiAgentTestHarness.Message("FINAL_" + calls)));
        }
        MultiAgentRuntime Runtime() => new(run, Model, _ => Task.CompletedTask, () => Task.CompletedTask,
            client: new() { Tools = NativeTools() });
        await Runtime().ExecuteAsync(new(), default);
        var followup = MultiAgentTestHarness.Message("FOLLOWUP", "user");
        followup["id"] = "user-followup";
        var request = new D { ["input"] = new List<object?> { followup } };
        await Runtime().ExecuteAsync(request, default);
        Assert.Equal(7, actor.CurrentTaskGeneration);
        Assert.Equal(8, Assert.Single(actor.PendingTasks).Generation);
        await Runtime().ExecuteAsync(request, default);
        Assert.Equal(4, calls);
        Assert.Equal(new[] { 7, 8 }, actor.CompletedTurns.Select(t => t.Generation));
        Assert.Empty(actor.PendingTasks);
        Assert.Single(actor.History.OfType<D>(), i => JsonDictionaryValue.String(i, "id") == "user-followup");
        Assert.True(run.Finished);
    }

    [Fact]
    public async Task DisconnectAfterFirstToolDoneKeepsEntireBatchPendingAndOnlyReplaysUnconsumedCalls()
    {
        var run = NativeRun();
        var calls = 0;
        D? delivered = null;
        using var stop = new CancellationTokenSource();
        Task<D> Model(D _, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(calls == 1
                ? MultiAgentTestHarness.Response(ClientCall("first_tool", new { }), ClientCall("second_tool", new { }))
                : MultiAgentTestHarness.Response(MultiAgentTestHarness.Message("FINAL")));
        }
        var first = new MultiAgentRuntime(run, Model, e =>
        {
            if (JsonDictionaryValue.String(e, "type") == "response.output_item.done")
            {
                delivered = WebSearchPayload.DeepCopyObject((D)e["item"]!);
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        }, () => Task.CompletedTask, client: new() { Tools = NativeTools() });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.ExecuteAsync(new(), stop.Token));
        Assert.NotNull(delivered);
        Assert.Equal(2, run.PendingCalls.Count);
        var resumed = new MultiAgentRuntime(run, Model, _ => Task.CompletedTask, () => Task.CompletedTask,
            client: new() { Tools = NativeTools() });
        var response = await resumed.ExecuteAsync(new() { ["input"] = new List<object?> { delivered, Output(delivered) } }, default);
        Assert.Equal(1, calls);
        var remaining = Assert.IsType<D>(Assert.Single(JsonDictionaryValue.List(response, "output")));
        Assert.Equal("second_tool", remaining["name"]);
        Assert.Equal(Assert.Single(run.PendingCalls).Key, remaining["call_id"]);
        await new MultiAgentRuntime(run, Model, _ => Task.CompletedTask, () => Task.CompletedTask,
            client: new() { Tools = NativeTools() }).ExecuteAsync(new() { ["input"] = new List<object?> { Output(delivered), Output(remaining) } }, default);
        Assert.Equal(2, calls);
        Assert.Equal(2, run.ReceivedCalls.Count);
        Assert.True(run.Finished);
    }

    [Fact]
    public async Task DisconnectDuringFinalDeliveryReplaysCommittedFinalWithoutAnotherModelAttempt()
    {
        var run = NativeRun();
        var calls = 0;
        using var stop = new CancellationTokenSource();
        Task<D> Model(D _, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(MultiAgentTestHarness.Response(MultiAgentTestHarness.Message("FINAL")));
        }
        var runtime = new MultiAgentRuntime(run, Model, e =>
        {
            if (JsonDictionaryValue.String(e, "type") == "response.output_item.done")
            {
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        }, () => Task.CompletedTask, client: new() { Tools = NativeTools() });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ExecuteAsync(new(), stop.Token));
        var response = await new MultiAgentRuntime(run, Model, _ => Task.CompletedTask, () => Task.CompletedTask,
            client: new() { Tools = NativeTools() }).ExecuteAsync(new(), default);
        Assert.Equal("completed", response["status"]);
        Assert.Contains("FINAL", WebSearchPayload.JsonDumps(response["output"]));
        Assert.Equal(1, calls);
        Assert.Single(run.Agents["/root"].CompletedTurns);
    }

    [Fact]
    public async Task InitialClientForkToolResultsAreNotMistakenForThisActorsPendingCalls()
    {
        var run = NativeRun();
        var inherited = new D { ["type"] = "function_call_output", ["call_id"] = "call_ma_parent_history", ["output"] = "parent result" };
        var calls = 0;
        var runtime = new MultiAgentRuntime(run, (_, _) => Task.FromResult(MultiAgentTestHarness.Response(
            MultiAgentTestHarness.Message(++calls == 1 ? "FIRST" : "SECOND"))), _ => Task.CompletedTask,
            () => Task.CompletedTask, client: new() { AgentName = "/root/child", Tools = NativeTools() });
        await runtime.ExecuteAsync(new() { ["input"] = new List<object?> { inherited } }, default);
        await runtime.ExecuteAsync(new()
        {
            ["input"] = new List<object?> { inherited, MultiAgentTestHarness.Message("next task", "user") }
        }, default);
        Assert.Equal(2, calls);
        Assert.Contains("call_ma_parent_history", run.ReceivedCalls);
        Assert.Empty(run.PendingCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientActorOutputsUseItsCanonicalIdentity(bool stream)
    {
        var run = NativeRun();
        var events = new List<D>();
        MultiAgentModelCall model = async (_, emit, ct) =>
        {
            var item = MultiAgentTestHarness.Message("child text");
            item["id"] = "source";
            if (stream)
            {
                var started = WebSearchPayload.DeepCopyObject(item);
                started["content"] = new List<object?>();
                await emit(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = started }, ct);
                await emit(new() { ["type"] = "response.output_text.delta", ["output_index"] = 0, ["delta"] = "child text" }, ct);
            }
            return MultiAgentTestHarness.Response(item);
        };
        var runtime = new MultiAgentRuntime(run, model, e => { events.Add(e); return Task.CompletedTask; },
            () => Task.CompletedTask, client: new() { AgentName = "/root/audit", ParentName = "/root", Tools = NativeTools() });
        var response = await runtime.ExecuteAsync(new(), CancellationToken.None);
        Assert.All(events.Where(e => e.ContainsKey("agent")), e =>
            Assert.Equal("/root/audit", JsonDictionaryValue.String((D)e["agent"]!, "agent_name")));
        var output = JsonDictionaryValue.List(response, "output").OfType<D>().Single();
        Assert.Equal("/root/audit", JsonDictionaryValue.String((D)output["agent"]!, "agent_name"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultipleNativeReportsEnterOneModelTurnInsteadOfSeparateTasks(bool pendingWait)
    {
        var run = NativeRun();
        run.ModelTurns = 1;
        var actor = run.Agents["/root"];
        actor.TaskStarted = pendingWait;
        actor.Status = pendingWait ? "tool_wait" : "completed";
        var input = new List<object?>();
        if (pendingWait)
        {
            var wait = ClientCall("wait_agent", new { timeout_ms = 1000 });
            wait["namespace"] = "collaboration";
            wait["call_id"] = "call_ma_wait";
            actor.History.Add(wait);
            run.PendingCalls["call_ma_wait"] = "/root";
            input.Add(new D { ["type"] = "function_call_output", ["call_id"] = "call_ma_wait", ["output"] = "updates available" });
        }
        input.Add(AgentReport("report-a", "/root/a", "result-a"));
        input.Add(AgentReport("report-b", "/root/b", "result-b"));
        var payloads = new List<D>();
        var runtime = new MultiAgentRuntime(run, (payload, _) =>
        {
            payloads.Add(WebSearchPayload.DeepCopyObject(payload));
            return Task.FromResult(MultiAgentTestHarness.Response(MultiAgentTestHarness.Message("combined answer")));
        }, _ => Task.CompletedTask, () => Task.CompletedTask, client: new() { Tools = NativeTools() });
        await runtime.ExecuteAsync(new() { ["input"] = input }, CancellationToken.None);
        var call = Assert.Single(payloads);
        Assert.Contains("result-a", WebSearchPayload.JsonDumps(call["input"]));
        Assert.Contains("result-b", WebSearchPayload.JsonDumps(call["input"]));
        Assert.Empty(actor.PendingTasks);
        Assert.Empty(actor.Mailbox);
        Assert.Single(actor.CompletedTurns);
    }

    [Fact]
    public async Task FailedSecondPreparationLeavesNoPartialToolHistory()
    {
        var run = NativeRun();
        var count = 0;
        var prepared = new List<string>();
        var rolledBack = new List<string>();
        var runtime = new MultiAgentRuntime(run, (_, _) => Task.FromResult(MultiAgentTestHarness.Response(NativeCall(), NativeCall())),
            _ => Task.CompletedTask, () => Task.CompletedTask, client: new()
            {
                Tools = NativeTools(), BeforeToolCall = (_, call, _, _) =>
                {
                    if (++count == 2) throw new BadRequestException("Second reservation failed.");
                    prepared.Add((string)call["call_id"]!);
                    return Task.CompletedTask;
                },
                RollbackToolCalls = (calls, ct) =>
                {
                    Assert.Equal(CancellationToken.None, ct);
                    Assert.DoesNotContain(run.Agents["/root"].History.OfType<D>(), item => JsonDictionaryValue.String(item, "type") == "function_call");
                    rolledBack.AddRange(calls.Select(call => (string)call["call_id"]!));
                    return Task.CompletedTask;
                }
            });
        var result = await runtime.ExecuteAsync(new(), CancellationToken.None);
        Assert.Equal("failed", result["status"]);
        Assert.Equal(2, count);
        Assert.Single(prepared);
        Assert.Equal(prepared, rolledBack);
        Assert.Empty(run.PendingCalls);
        Assert.DoesNotContain(run.Agents["/root"].History.OfType<D>(), item => JsonDictionaryValue.String(item, "type") == "function_call");
    }

    [Fact]
    public async Task EntireNativeCallBatchIsValidatedBeforePreparingAnyCall()
    {
        var run = NativeRun();
        var invalid = NativeCall();
        invalid["arguments"] = "{}";
        var prepared = 0;
        var runtime = new MultiAgentRuntime(run, (_, _) => Task.FromResult(MultiAgentTestHarness.Response(NativeCall(), invalid)),
            _ => Task.CompletedTask, () => Task.CompletedTask, client: new()
            {
                Tools = NativeTools(), BeforeToolCall = (_, _, _, _) => { prepared++; return Task.CompletedTask; }
            });
        var result = await runtime.ExecuteAsync(new(), CancellationToken.None);
        Assert.Equal("failed", result["status"]);
        Assert.Equal(0, prepared);
        Assert.Empty(run.PendingCalls);
        Assert.DoesNotContain(run.Agents["/root"].History.OfType<D>(), item => JsonDictionaryValue.String(item, "type") == "function_call");
    }

    [Fact]
    public async Task CancellationDuringPreparationStillRollsBackCompletedReservations()
    {
        var run = NativeRun();
        using var stop = new CancellationTokenSource();
        var prepared = 0;
        var rolledBack = 0;
        var runtime = new MultiAgentRuntime(run, (_, _) => Task.FromResult(MultiAgentTestHarness.Response(NativeCall(), NativeCall())),
            _ => Task.CompletedTask, () => Task.CompletedTask, client: new()
            {
                Tools = NativeTools(), BeforeToolCall = (_, _, _, ct) =>
                {
                    if (++prepared == 2) stop.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                RollbackToolCalls = (calls, ct) =>
                {
                    Assert.False(ct.IsCancellationRequested);
                    rolledBack += calls.Count;
                    return Task.CompletedTask;
                }
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ExecuteAsync(new(), stop.Token));
        Assert.Equal(1, rolledBack);
        Assert.Empty(run.PendingCalls);
        Assert.DoesNotContain(run.Agents["/root"].History.OfType<D>(), item => JsonDictionaryValue.String(item, "type") == "function_call");
    }

    [Theory]
    [InlineData("stable-report-id")]
    [InlineData(null)]
    public async Task ReplayedNativeReportIsDeduplicatedAcrossDifferentResponseIds(string? id)
    {
        var run = NativeRun();
        run.ModelTurns = 1;
        run.Agents["/root"].Status = "completed";
        var calls = 0;
        for (var round = 0; round < 2; round++)
        {
            var runtime = new MultiAgentRuntime(run, (_, _) =>
            {
                calls++;
                return Task.FromResult(MultiAgentTestHarness.Response(MultiAgentTestHarness.Message("done")));
            }, _ => Task.CompletedTask, () => Task.CompletedTask, client: new() { Tools = NativeTools() });
            await runtime.ExecuteAsync(new()
            {
                ["previous_response_id"] = "previous-" + round,
                ["input"] = new List<object?> { AgentReport(id, "/root/a", "report text") }
            }, CancellationToken.None);
        }
        Assert.Equal(1, calls);
        Assert.Single(run.Agents["/root"].CompletedTurns);
    }

    [Fact]
    public async Task NativeToolsRemainAvailableToModelAfterContinuationUpdates()
    {
        var run = NativeRun();
        var tools = NativeTools();
        run.Template = tools.ApplyToTemplate(run.Template);
        D? captured = null;
        var runtime = new MultiAgentRuntime(run, (payload, _) =>
        {
            captured = payload;
            return Task.FromResult(MultiAgentTestHarness.Response(MultiAgentTestHarness.Message("done")));
        }, _ => Task.CompletedTask, () => Task.CompletedTask, client: new() { Tools = tools, AgentName = "/root/a", ParentName = "/root" });
        await runtime.ExecuteAsync(new(), CancellationToken.None);
        var definitions = JsonDictionaryValue.List(captured!, "tools").OfType<D>().ToList();
        Assert.Contains(definitions, tool => Equals(tool["name"], "collaboration"));
        Assert.DoesNotContain(definitions, tool => JsonDictionaryValue.String(tool, "name").StartsWith("ocxp_ma_", StringComparison.Ordinal));
        Assert.Equal(run.Id + ":/root/a", captured!["prompt_cache_key"]);
        Assert.Contains("Your identity is /root/a. Parent: /root.", WebSearchPayload.JsonDumps(captured["input"]));
        Assert.DoesNotContain("Do not call client-side collaboration", WebSearchPayload.JsonDumps(captured["input"]));
        Assert.Single(run.Agents);
    }

    [Fact]
    public async Task NativeAgentMessageResumesCompletedAgentAndKeepsMessageIdentity()
    {
        var run = NativeRun();
        run.ModelTurns = 1;
        run.Finished = true;
        run.Agents["/root"].Status = "completed";
        D? captured = null;
        var runtime = new MultiAgentRuntime(run, (payload, _) =>
        {
            captured = payload;
            return Task.FromResult(MultiAgentTestHarness.Response(MultiAgentTestHarness.Message("received")));
        }, _ => Task.CompletedTask, () => Task.CompletedTask, client: new() { Tools = NativeTools() });
        await runtime.ExecuteAsync(new()
        {
            ["input"] = new List<object?> { new D
            {
                ["type"] = "agent_message", ["id"] = "native-message", ["author"] = "/root/a",
                ["recipient"] = "/root", ["content"] = "child report"
            } }
        }, CancellationToken.None);
        Assert.NotNull(captured);
        var message = Assert.Single(JsonDictionaryValue.List(captured, "input").OfType<D>(), item => Equals(JsonDictionaryValue.Get(item, "id"), "native-message"));
        Assert.Equal("message", message["type"]);
        Assert.Equal("user", message["role"]);
        Assert.Contains("child report", WebSearchPayload.JsonDumps(message));
    }

    [Fact]
    public async Task CallsAreRegisteredBeforeHistoryAndClientCompletionIncludingOrdinaryTools()
    {
        var run = NativeRun();
        JsonDictionaryValue.List(run.Template, "tools").Add(new D { ["type"] = "custom", ["name"] = "exec" });
        var registered = new HashSet<string>();
        var native = NativeCall();
        var custom = new D { ["type"] = "custom_tool_call", ["call_id"] = "custom-call", ["name"] = "exec", ["input"] = "text(1);" };
        var hooks = new MultiAgentClientRuntimeHooks
        {
            Tools = NativeTools(),
            BeforeToolCall = (agent, call, history, ct) =>
            {
                Assert.Equal(CancellationToken.None, ct);
                Assert.DoesNotContain(history.OfType<D>(), item => JsonDictionaryValue.String(item, "type") is "function_call" or "custom_tool_call");
                Assert.DoesNotContain(agent.History.OfType<D>(), item => Equals(JsonDictionaryValue.Get(item, "call_id"), call["call_id"]));
                registered.Add((string)call["call_id"]!);
                return Task.CompletedTask;
            }
        };
        var runtime = new MultiAgentRuntime(run, (_, _) => Task.FromResult(MultiAgentTestHarness.Response(native, custom)), e =>
        {
            if (JsonDictionaryValue.String(e, "type") == "response.output_item.done")
            {
                var item = (D)e["item"]!;
                Assert.Contains((string)item["call_id"]!, registered);
                Assert.Contains((string)item["call_id"]!, run.PendingCalls.Keys);
            }
            return Task.CompletedTask;
        }, () => Task.CompletedTask, client: hooks);
        var response = await runtime.ExecuteAsync(new(), CancellationToken.None);
        Assert.Equal(2, registered.Count);
        Assert.Equal(2, run.PendingCalls.Count);
        Assert.Single(run.Agents);
        var forwarded = Assert.Single(JsonDictionaryValue.List(response, "output").OfType<D>(), item => Equals(item["name"], "spawn_agent"));
        Assert.Equal("collaboration", forwarded["namespace"]);
        Assert.Equal(native["arguments"], forwarded["arguments"]);
    }

    [Fact]
    public async Task FailedResultHookRetainsPendingCallAndSuccessfulRetryIsDeduplicated()
    {
        var run = NativeRun();
        var attempts = 0;
        var models = 0;
        var hooks = new MultiAgentClientRuntimeHooks
        {
            Tools = NativeTools(),
            ToolResult = (call, result, ct) =>
            {
                Assert.Contains((string)call["call_id"]!, run.PendingCalls.Keys);
                Assert.DoesNotContain((string)call["call_id"]!, run.ReceivedCalls);
                Assert.Equal("collaboration", call["namespace"]);
                Assert.Equal("child-created", result["output"]);
                if (++attempts == 1) throw new BadRequestException("Temporary binding failure.");
                return Task.CompletedTask;
            }
        };
        Task<D> Model(D payload, CancellationToken ct)
        {
            if (++models == 1) return Task.FromResult(MultiAgentTestHarness.Response(NativeCall()));
            Assert.Contains("child-created", WebSearchPayload.JsonDumps(payload["input"]));
            return Task.FromResult(MultiAgentTestHarness.Response(MultiAgentTestHarness.Message("done")));
        }
        MultiAgentRuntime Runtime() => new(run, Model, _ => Task.CompletedTask, () => Task.CompletedTask, client: hooks);
        var first = await Runtime().ExecuteAsync(new(), CancellationToken.None);
        var call = (D)JsonDictionaryValue.List(first, "output").Single()!;
        var continuation = new D { ["input"] = new List<object?> { Output(call) } };
        await Assert.ThrowsAsync<BadRequestException>(() => Runtime().ExecuteAsync(continuation, CancellationToken.None));
        Assert.Single(run.PendingCalls);
        Assert.Empty(run.ReceivedCalls);
        await Runtime().ExecuteAsync(continuation, CancellationToken.None);
        await Runtime().ExecuteAsync(continuation, CancellationToken.None);
        Assert.Equal(2, attempts);
        Assert.Equal(2, models);
        Assert.Empty(run.PendingCalls);
        Assert.Single(run.ReceivedCalls);
    }

    [Fact]
    public async Task WebSocketInjectionPassesCancellationAndRunsNativeResultHook()
    {
        var run = NativeRun();
        var injections = Channel.CreateUnbounded<MultiAgentInjection>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var calls = 0;
        var results = 0;
        var hooks = new MultiAgentClientRuntimeHooks
        {
            Tools = NativeTools(),
            ToolResult = (_, _, ct) =>
            {
                Assert.Equal(timeout.Token, ct);
                results++;
                return Task.CompletedTask;
            }
        };
        var runtime = new MultiAgentRuntime(run, (_, _) => Task.FromResult(++calls == 1
            ? MultiAgentTestHarness.Response(NativeCall()) : MultiAgentTestHarness.Response(MultiAgentTestHarness.Message("done"))), e =>
        {
            if (JsonDictionaryValue.String(e, "type") == "response.output_item.done"
                && e["item"] is D call && JsonDictionaryValue.String(call, "type") == "function_call")
                injections.Writer.TryWrite(new() { ResponseId = run.LastResponseId, Input = [Output(call)] });
            return Task.CompletedTask;
        }, () => Task.CompletedTask, client: hooks);
        await runtime.ExecuteAsync(new(), timeout.Token, injections.Reader);
        Assert.Equal(1, results);
        Assert.Equal(2, calls);
        Assert.True(run.Finished);
    }

    [Fact]
    public async Task NativeModeRejectsServerOnlyActionsWithoutSpawningHiddenAgents()
    {
        var run = NativeRun();
        var events = new List<D>();
        var runtime = new MultiAgentRuntime(run, (_, _) => Task.FromResult(MultiAgentTestHarness.Response(
            ClientCall("ocxp_ma_spawn_agent", new { task_name = "hidden", message = "task" }))), e =>
        {
            events.Add(e);
            return Task.CompletedTask;
        }, () => Task.CompletedTask, client: new() { Tools = NativeTools() });
        var result = await runtime.ExecuteAsync(new(), CancellationToken.None);
        Assert.Equal("failed", result["status"]);
        Assert.Single(run.Agents);
        Assert.Empty(run.PendingCalls);
        Assert.DoesNotContain(events, e => Equals(e["type"], "response.output_item.done"));
    }

    [Fact]
    public async Task RejectedRegistrationNeverBecomesAnExecutableCompletedCall()
    {
        var run = NativeRun();
        var events = new List<D>();
        var runtime = new MultiAgentRuntime(run, (_, _) => Task.FromResult(MultiAgentTestHarness.Response(NativeCall())), e =>
        {
            events.Add(e);
            return Task.CompletedTask;
        }, () => Task.CompletedTask, client: new()
        {
            Tools = NativeTools(), BeforeToolCall = (_, _, _, _) => throw new BadRequestException("Binding rejected.")
        });
        var result = await runtime.ExecuteAsync(new(), CancellationToken.None);
        Assert.Equal("failed", result["status"]);
        Assert.Empty(run.PendingCalls);
        Assert.DoesNotContain(run.Agents["/root"].History.OfType<D>(), item => Equals(item["type"], "function_call"));
        Assert.DoesNotContain(events, e => Equals(e["type"], "response.output_item.done"));
    }

    [Fact]
    public async Task ReplayedNativeMessageIsDeduplicatedAndLegacyAgentMessagesRemainUnchanged()
    {
        async Task<int> Resume(MultiAgentClientRuntimeHooks? client)
        {
            var run = NativeRun();
            run.ModelTurns = 1;
            run.Agents["/root"].Status = "completed";
            var calls = 0;
            var request = new D { ["input"] = new List<object?> { new D
            {
                ["type"] = "agent_message", ["id"] = "completion-id", ["author"] = "/root/a",
                ["recipient"] = "/root", ["content"] = "completed"
            } } };
            for (var n = 0; n < 2; n++)
            {
                var runtime = new MultiAgentRuntime(run, (_, _) =>
                {
                    calls++;
                    return Task.FromResult(MultiAgentTestHarness.Response(MultiAgentTestHarness.Message("done")));
                }, _ => Task.CompletedTask, () => Task.CompletedTask, client: client);
                await runtime.ExecuteAsync(request, CancellationToken.None);
            }
            return calls;
        }
        Assert.Equal(1, await Resume(new() { Tools = NativeTools() }));
        Assert.Equal(0, await Resume(null));
    }

    private static MultiAgentRun NativeRun()
    {
        var run = MultiAgentTestHarness.Run();
        run.Template = NativeTools().ApplyToTemplate(run.Template);
        foreach (var name in new[] { "first_tool", "second_tool" })
            JsonDictionaryValue.List(run.Template, "tools").Add(new D
            {
                ["type"] = "function", ["name"] = name,
                ["parameters"] = new D { ["type"] = "object", ["properties"] = new D() }
            });
        return run;
    }

    private static D ClientCall(string name, object arguments)
    {
        var call = MultiAgentTestHarness.Call(name, arguments);
        call["call_id"] = "upstream-" + Guid.NewGuid().ToString("N");
        return call;
    }

    private static D NativeCall()
    {
        var call = ClientCall("spawn_agent", new { task_name = "child", message = "task" });
        call["namespace"] = "collaboration";
        return call;
    }

    private static D Output(D call) => new()
    {
        ["type"] = "function_call_output", ["call_id"] = call["call_id"], ["output"] = "child-created"
    };

    private static D AgentReport(string? id, string author, string content)
    {
        var message = new D { ["type"] = "agent_message", ["author"] = author, ["recipient"] = "/root", ["content"] = content };
        if (id is not null) message["id"] = id;
        return message;
    }

    private static MultiAgentClientTools NativeTools() => MultiAgentClientTools.FromDefinitions(new List<object?>
    {
        new D { ["type"] = "namespace", ["name"] = "collaboration", ["tools"] = new List<object?>
        {
            new D { ["type"] = "function", ["name"] = "spawn_agent", ["parameters"] = new D
            {
                ["type"] = "object", ["properties"] = new D
                {
                    ["task_name"] = new D { ["type"] = "string" }, ["message"] = new D { ["type"] = "string" }
                }, ["required"] = new List<object?> { "task_name", "message" }, ["additionalProperties"] = false
            } }
        } }
    });
}

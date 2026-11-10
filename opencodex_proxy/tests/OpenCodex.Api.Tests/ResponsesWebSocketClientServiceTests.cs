using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;
using static OpenCodex.Api.Tests.MultiAgentApiTestContext;
using static OpenCodex.Api.Tests.MultiAgentClientResponseTests;

namespace OpenCodex.Api.Tests;

public sealed class ResponsesWebSocketClientServiceTests
{
    [Fact]
    public async Task PrewarmAndContinuationExpandConnectionLocalHistoryOnce()
    {
        using var context = new MultiAgentApiTestContext(
            ctx => Stream(ctx, Events(Terminal(Message("ANSWER")))));
        context.CatalogEnabled = false;
        var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        try
        {
            var prewarm = Request();
            prewarm["model"] = "gpt-6-luna";
            prewarm["type"] = "response.create";
            prewarm["generate"] = false;
            socket.ClientSend(prewarm);

            var warm = (D)(await socket.Until("response.completed", context.Lifetime.Token))["response"]!;
            Assert.Empty(context.FakeEndpoint.Calls);

            var actual = Request();
            actual["model"] = "gpt-6-luna";
            actual["type"] = "response.create";
            actual["previous_response_id"] = warm["id"];
            actual["input"] = new List<object?>();
            socket.ClientSend(actual);

            var completed = (D)(await socket.Until("response.completed", context.Lifetime.Token))["response"]!;
            Assert.Single(context.FakeEndpoint.Calls);
            Assert.Equal(
                "work",
                JsonDictionaryValue.String(
                    (D)JsonDictionaryValue.List(context.FakeEndpoint.Calls[0].Payload!, "input")[0]!,
                    "content"));

            var next = Request();
            next["model"] = "gpt-6-luna";
            next["type"] = "response.create";
            next["previous_response_id"] = completed["id"];
            next["input"] = new List<object?>
            {
                new D { ["type"] = "message", ["role"] = "user", ["content"] = "next" }
            };
            socket.ClientSend(next);

            await socket.Until("response.completed", context.Lifetime.Token);
            Assert.Equal(2, context.FakeEndpoint.Calls.Count);
            var projected = JsonDictionaryValue.List(context.FakeEndpoint.Calls[1].Payload!, "input");
            Assert.Equal(3, projected.Count);
            Assert.Contains("ANSWER", System.Text.Json.JsonSerializer.Serialize(projected[1]));
            Assert.Contains("next", System.Text.Json.JsonSerializer.Serialize(projected[2]));

            var clientPayload = Assert.IsType<D>(context.FakeEndpoint.Calls[1].ClientPayload);
            Assert.Equal(completed["id"], JsonDictionaryValue.String(clientPayload, "previous_response_id"));
            var clientInput = JsonDictionaryValue.List(clientPayload, "input");
            Assert.Single(clientInput);
            Assert.Contains("next", System.Text.Json.JsonSerializer.Serialize(clientInput[0]));
        }
        finally
        {
            socket.ClientClose();
            await operation;
        }
    }

    [Fact]
    public async Task MissingPreviousResponseReturnsRecoverableProtocolError()
    {
        using var context = new MultiAgentApiTestContext(
            ctx => Stream(ctx, Events(Terminal(Message("unexpected")))));
        context.CatalogEnabled = false;
        var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        try
        {
            var request = Request();
            request["model"] = "gpt-6-luna";
            request["type"] = "response.create";
            request["previous_response_id"] = "missing";
            socket.ClientSend(request);

            var error = await socket.Until("error", context.Lifetime.Token);
            Assert.Equal(
                "previous_response_not_found",
                JsonDictionaryValue.String(
                    JsonDictionaryValue.Object(error, "error", WebSearchPayload.DeepCopyObject),
                    "code"));
            Assert.Empty(context.FakeEndpoint.Calls);
        }
        finally
        {
            socket.ClientClose();
            await operation;
        }
    }

    [Fact]
    public async Task CreateAfterCompletedIsNotRejectedBeforeExecuteUnwinds()
    {
        var terminalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedSends = 0;
        var modelCalls = 0;
        using var context = new MultiAgentApiTestContext(
            ctx => Stream(ctx, Events(Terminal(Message("CALL_" + Interlocked.Increment(ref modelCalls))))));
        context.CatalogEnabled = false;
        var socket = context.Socket();
        socket.OnServerSend = async (payload, ct) =>
        {
            if (Type(payload) == "response.completed"
                && Interlocked.Increment(ref completedSends) == 1)
            {
                terminalStarted.TrySetResult();
                await releaseTerminal.Task.WaitAsync(ct);
            }
        };
        var operation = context.Service.ResponsesWebSocket();
        try
        {
            var first = Request();
            first["type"] = "response.create";
            socket.ClientSend(first);
            await socket.Until("response.completed", context.Lifetime.Token);
            await terminalStarted.Task.WaitAsync(context.Lifetime.Token);

            while (socket.Received.Reader.TryRead(out _)) { }
            var second = Request();
            second["type"] = "response.create";
            socket.ClientSend(second);
            await socket.Received.Reader.ReadAsync(context.Lifetime.Token);
            releaseTerminal.TrySetResult();

            var completed = (D)(await socket.Until("response.completed", context.Lifetime.Token))["response"]!;
            Assert.Contains("CALL_2", System.Text.Json.JsonSerializer.Serialize(completed));
            Assert.Equal(2, modelCalls);
        }
        finally
        {
            releaseTerminal.TrySetResult();
            socket.ClientClose();
            await operation;
        }
    }

    [Fact]
    public async Task BranchingFromEarlierResponseKeepsThatResponseHistory()
    {
        var modelCalls = 0;
        using var context = new MultiAgentApiTestContext(
            ctx => Stream(ctx, Events(Terminal(Message(++modelCalls == 1 ? "FIRST" : "SECOND")))));
        context.CatalogEnabled = false;
        var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        try
        {
            var initial = Request();
            initial["type"] = "response.create";
            socket.ClientSend(initial);
            var first = (D)(await socket.Until("response.completed", context.Lifetime.Token))["response"]!;

            var next = Request();
            next["type"] = "response.create";
            next["previous_response_id"] = first["id"];
            next["input"] = new List<object?>
            {
                new D { ["type"] = "message", ["role"] = "user", ["content"] = "next" }
            };
            socket.ClientSend(next);
            await socket.Until("response.completed", context.Lifetime.Token);

            var branch = Request();
            branch["type"] = "response.create";
            branch["previous_response_id"] = first["id"];
            branch["input"] = new List<object?>
            {
                new D { ["type"] = "message", ["role"] = "user", ["content"] = "branch" }
            };
            socket.ClientSend(branch);
            await socket.Until("response.completed", context.Lifetime.Token);

            var projected = JsonDictionaryValue.List(context.FakeEndpoint.Calls[2].Payload!, "input");
            Assert.Equal(3, projected.Count);
            Assert.Contains("work", System.Text.Json.JsonSerializer.Serialize(projected[0]));
            Assert.Contains("FIRST", System.Text.Json.JsonSerializer.Serialize(projected[1]));
            Assert.Contains("branch", System.Text.Json.JsonSerializer.Serialize(projected[2]));

            var clientPayload = Assert.IsType<D>(context.FakeEndpoint.Calls[2].ClientPayload);
            Assert.Single(JsonDictionaryValue.List(clientPayload, "input"));
            Assert.Equal(first["id"], JsonDictionaryValue.String(clientPayload, "previous_response_id"));
        }
        finally
        {
            socket.ClientClose();
            await operation;
        }
    }

    [Fact]
    public async Task InterruptCancelsActiveGenericResponseAndKeepsConnectionOpen()
    {
        var calls = 0;
        var modelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modelCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = new MultiAgentApiTestContext(async ctx =>
        {
            if (Interlocked.Increment(ref calls) > 1)
                return await Stream(ctx, Events(Terminal(Message("NEXT"))));

            async IAsyncEnumerable<D> Waiting(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new D
                {
                    ["type"] = "response.created",
                    ["response"] = new D
                    {
                        ["id"] = "resp_generic_interrupt",
                        ["object"] = "response",
                        ["status"] = "in_progress",
                        ["model"] = "gpt-6-luna",
                        ["output"] = new List<object?>()
                    }
                };
                modelStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    modelCancelled.TrySetResult();
                    throw;
                }
            }

            return await Stream(ctx, Waiting(ctx.CancellationToken));
        });
        context.CatalogEnabled = false;
        var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        try
        {
            var create = Request();
            create["model"] = "gpt-6-luna";
            create["type"] = "response.create";
            socket.ClientSend(create);
            var created = (D)(await socket.Until("response.created", context.Lifetime.Token))["response"]!;
            var responseId = (string)created["id"]!;
            await modelStarted.Task.WaitAsync(context.Lifetime.Token);

            using var interruptTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.Lifetime.Token);
            interruptTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            socket.ClientSend(create);
            var inProgress = await socket.Until("error", interruptTimeout.Token);
            Assert.Equal(
                "response_in_progress",
                JsonDictionaryValue.String(
                    JsonDictionaryValue.Object(inProgress, "error", WebSearchPayload.DeepCopyObject),
                    "code"));
            Assert.Equal(1, calls);

            socket.ClientSend(new D
            {
                ["type"] = "response.interrupt",
                ["response_id"] = responseId,
                ["mode"] = "discard_partial_items"
            });

            var accepted = await socket.Until("response.interrupt.accepted", interruptTimeout.Token);
            Assert.Equal(responseId, accepted["response_id"]);
            await modelCancelled.Task.WaitAsync(interruptTimeout.Token);
            Assert.Equal(System.Net.WebSockets.WebSocketState.Open, socket.State);
            Assert.DoesNotContain(socket.Sent, item => Type(item) == "response.failed");
            var recoverableError = Assert.Single(socket.Sent, item => Type(item) == "error");
            Assert.Equal(
                "response_in_progress",
                JsonDictionaryValue.String(
                    JsonDictionaryValue.Object(recoverableError, "error", WebSearchPayload.DeepCopyObject),
                    "code"));

            socket.ClientSend(create);
            var completed = (D)(await socket.Until("response.completed", context.Lifetime.Token))["response"]!;
            Assert.Contains("NEXT", System.Text.Json.JsonSerializer.Serialize(completed));

            socket.ClientSend(new D
            {
                ["type"] = "response.interrupt",
                ["response_id"] = responseId,
                ["mode"] = "discard_partial_items"
            });
            var completedInterrupt = await socket.Until("response.interrupt.accepted", interruptTimeout.Token);
            Assert.Equal(responseId, completedInterrupt["response_id"]);

            socket.ClientSend(new D
            {
                ["type"] = "response.interrupt",
                ["response_id"] = "missing",
                ["mode"] = "discard_partial_items"
            });
            var missing = await socket.Until("response.interrupt.failed", interruptTimeout.Token);
            Assert.Equal("missing", missing["response_id"]);
            Assert.Equal(
                "response_not_found",
                JsonDictionaryValue.String(
                    JsonDictionaryValue.Object(missing, "error", WebSearchPayload.DeepCopyObject),
                    "code"));
            Assert.Equal(System.Net.WebSockets.WebSocketState.Open, socket.State);
            Assert.Equal(2, calls);
        }
        finally
        {
            socket.ClientClose();
            context.Lifetime.Cancel();
            await operation;
        }
    }
}

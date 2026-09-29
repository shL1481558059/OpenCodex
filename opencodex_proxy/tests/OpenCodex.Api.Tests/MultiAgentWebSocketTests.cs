using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;
using static OpenCodex.Api.Tests.MultiAgentApiTestContext;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentWebSocketTests
{
    private static D Create() { var request = Request(); request["type"] = "response.create"; return request; }
    private static D Injection(string response, string call) => new()
    {
        ["type"] = "response.inject", ["response_id"] = response,
        ["input"] = new List<object?> { new D { ["type"] = "function_call_output", ["call_id"] = call, ["output"] = "42" } }
    };

    [Fact]
    public async Task ToolInjectionAcknowledgesThenCompletionRejectsLaterAndUnknownResponses()
    {
        var calls = 0;
        using var test = new MultiAgentApiTestContext(ctx =>
        {
            if (++calls == 1)
                return Stream(ctx, Events(Terminal(new D { ["type"] = "function_call", ["id"] = "tool", ["name"] = "lookup", ["call_id"] = "up_call", ["arguments"] = "{}" })));
            Assert.Contains("42", WebSearchPayload.JsonDumps(ctx.Payload!["input"]));
            return Stream(ctx, Events(Terminal(Message("VERIFIED:42"))));
        });
        var socket = test.Socket();
        var operation = test.Service.ResponsesWebSocket();
        socket.ClientSend(Create());
        var created = await socket.Until("response.created", test.Lifetime.Token);
        var responseId = (string)((D)created["response"]!)["id"]!;
        var done = await socket.Until("response.output_item.done", test.Lifetime.Token);
        var callId = (string)((D)done["item"]!)["call_id"]!;
        var injection = Injection(responseId, callId);
        socket.ClientSend(injection);
        var ack = await socket.Until("response.inject.created", test.Lifetime.Token);
        Assert.Equal(responseId, ack["response_id"]);
        await socket.Until("response.completed", test.Lifetime.Token);
        socket.ClientSend(injection);
        var late = await socket.Until("response.inject.failed", test.Lifetime.Token);
        Assert.Equal("response_already_completed", ((D)late["error"]!)["code"]);
        Assert.Equal(WebSearchPayload.JsonDumps(injection["input"]), WebSearchPayload.JsonDumps(late["input"]));
        socket.ClientSend(Injection("unknown", callId));
        var unknown = await socket.Until("response.inject.failed", test.Lifetime.Token);
        Assert.Equal("response_not_found", ((D)unknown["error"]!)["code"]);
        socket.ClientClose();
        await operation;
        Assert.Single(socket.Sent, e => Type(e) == "response.completed");
        var sequence = socket.Sent.Where(e => e.ContainsKey("sequence_number")).Select(e => Convert.ToInt64(e["sequence_number"])).ToArray();
        Assert.True(sequence.Zip(sequence.Skip(1)).All(pair => pair.First < pair.Second));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"type\":\"response.inject\",\"response_id\":\"x\",\"input\":[]}")]
    [InlineData("{\"type\":\"response.inject\",\"response_id\":\"x\",\"input\":[{\"type\":\"function_call_output\"}]}")]
    [InlineData("{\"type\":\"unknown\"}")]
    public async Task InvalidSchemaReturnsErrorAndCloses(string input)
    {
        using var test = new MultiAgentApiTestContext(_ => throw new Exception("must not call"));
        var socket = test.Socket();
        var operation = test.Service.ResponsesWebSocket();
        socket.ClientRaw(input);
        Assert.Equal(400L, Convert.ToInt64((await socket.Until("error", test.Lifetime.Token))["status"]));
        await operation;
        Assert.NotEqual(System.Net.WebSockets.WebSocketState.Open, socket.State);
        Assert.Empty(test.FakeEndpoint.Calls);
    }

    [Fact]
    public async Task DisconnectCancelsActiveModel()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var test = new MultiAgentApiTestContext(async ctx =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ctx.CancellationToken); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            throw new Exception("unreachable");
        });
        var socket = test.Socket();
        var operation = test.Service.ResponsesWebSocket();
        socket.ClientSend(Create());
        await started.Task.WaitAsync(test.Lifetime.Token);
        socket.ClientClose();
        await cancelled.Task.WaitAsync(test.Lifetime.Token);
        await operation;
        Assert.DoesNotContain(socket.Sent, e => Type(e) == "response.completed");
    }

    [Fact]
    public async Task ConcurrentCreateIsRejectedWithoutCancellingTheExistingModel()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var test = new MultiAgentApiTestContext(async ctx =>
        {
            started.TrySetResult();
            using var registration = ctx.CancellationToken.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            throw new Exception("unreachable");
        });
        var socket = test.Socket();
        var operation = test.Service.ResponsesWebSocket();
        socket.ClientSend(Create());
        await started.Task.WaitAsync(test.Lifetime.Token);
        socket.ClientSend(Create());
        var error = await socket.Until("error", test.Lifetime.Token);
        Assert.Contains("Only one response", (string)((D)error["error"]!)["message"]!);
        Assert.False(operation.IsCompleted);
        Assert.False(cancelled.Task.IsCompleted);
        socket.ClientClose();
        await cancelled.Task.WaitAsync(test.Lifetime.Token);
        await operation;
        Assert.Single(test.FakeEndpoint.Calls);
    }

    [Fact]
    public async Task CatalogDisabledModelNeverCallsUpstream()
    {
        using var test = new MultiAgentApiTestContext(_ => throw new Exception("must not call"));
        test.CatalogEnabled = false;
        var socket = test.Socket();
        var operation = test.Service.ResponsesWebSocket();
        socket.ClientSend(Create());
        await socket.Until("error", test.Lifetime.Token);
        await operation;
        Assert.Empty(test.FakeEndpoint.Calls);
    }

    [Fact]
    public async Task ConcurrentCreateCannotSilentlyDropAnAlreadyQueuedInjection()
    {
        var releaseToolEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var test = new MultiAgentApiTestContext(ctx => Stream(ctx, Events(Terminal(new D
        {
            ["type"] = "function_call", ["name"] = "lookup", ["id"] = "tool", ["call_id"] = "up", ["arguments"] = "{}"
        }))));
        var socket = test.Socket();
        socket.OnServerSend = async (item, ct) =>
        {
            if (Type(item) == "response.output_item.done" && Type((D)item["item"]!) == "function_call")
                await releaseToolEvent.Task.WaitAsync(ct);
        };
        var operation = test.Service.ResponsesWebSocket();
        socket.ClientSend(Create());
        await socket.Received.Reader.ReadAsync(test.Lifetime.Token);
        var created = await socket.Until("response.created", test.Lifetime.Token);
        var id = (string)((D)created["response"]!)["id"]!;
        var tool = await socket.Until("response.output_item.done", test.Lifetime.Token);
        socket.ClientSend(Injection(id, (string)((D)tool["item"]!)["call_id"]!));
        socket.ClientSend(Create());
        Assert.Equal("response.inject", Type(await socket.Received.Reader.ReadAsync(test.Lifetime.Token)));
        Assert.Equal("response.create", Type(await socket.Received.Reader.ReadAsync(test.Lifetime.Token)));
        releaseToolEvent.SetResult();
        D acknowledgement;
        do { acknowledgement = await socket.Outgoing.Reader.ReadAsync(test.Lifetime.Token); }
        while (Type(acknowledgement) is not ("response.inject.created" or "response.inject.failed"));
        Assert.Equal(id, JsonDictionaryValue.String(acknowledgement, "response_id"));
        Assert.False(operation.IsCompleted);
        socket.ClientClose();
        await operation;
    }
}

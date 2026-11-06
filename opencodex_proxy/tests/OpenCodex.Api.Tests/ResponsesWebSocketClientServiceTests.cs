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
}

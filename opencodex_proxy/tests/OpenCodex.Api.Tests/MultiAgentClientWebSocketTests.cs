using System.Text.Json;
using OpenCodex.Core.Services.MultiAgent;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;
using static OpenCodex.Api.Tests.MultiAgentApiTestContext;
using static OpenCodex.Api.Tests.MultiAgentClientResponseTests;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentClientWebSocketTests
{
    [Fact]
    public async Task RepeatedCreatesDoNotReuseTheConnectionHeadersTurnIdentity()
    {
        using var root = Root(ctx => Stream(ctx, Events(Terminal(Message("DONE")))));
        root.Http.Request.Headers["x-codex-turn-metadata"] = """{"thread_id":"root-thread","agent_name":"/root","turn_id":"connection-turn"}""";
        var socket = root.Socket();
        var operation = root.Service.ResponsesWebSocket();
        try
        {
            var create = ClientRequest();
            create["type"] = "response.create";
            socket.ClientSend(create);
            var first = (D)(await socket.Until("response.completed", root.Lifetime.Token))["response"]!;
            socket.ClientSend(create);
            var second = (D)(await socket.Until("response.completed", root.Lifetime.Token))["response"]!;
            Assert.Equal(2, root.FakeEndpoint.Calls.Count);
            Assert.NotEqual(first["id"], second["id"]);
        }
        finally { socket.ClientClose(); await operation; }
    }

    [Fact]
    public async Task WebSocketNativeToolsWithoutIdentityCannotSilentlyBecomeLegacyAgents()
    {
        using var context = new MultiAgentApiTestContext(ctx => Stream(ctx, Events(Terminal(Message("unexpected legacy")))));
        var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        var create = ClientRequest();
        create["type"] = "response.create";
        socket.ClientSend(create);
        try
        {
            var first = await socket.Outgoing.Reader.ReadAsync(context.Lifetime.Token);
            Assert.Equal("error", Type(first));
            Assert.Contains("thread", JsonSerializer.Serialize(first), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(context.FakeEndpoint.Calls);
        }
        finally
        {
            socket.ClientClose();
            await operation;
        }
    }

    [Fact]
    public async Task WebSocketToolResultUsesNextCreateAndSharesNativeIdentityWithHttpChild()
    {
        var calls = 0;
        using var root = Root(ctx => Stream(ctx, Events(Terminal(++calls == 1
            ? Spawn("spawn_a", "audit", "gpt-6-luna", "audit source") : Message("PARENT DONE")))));
        var socket = root.Socket();
        var operation = root.Service.ResponsesWebSocket();
        try
        {
            var create = ClientRequest();
            create["type"] = "response.create";
            socket.ClientSend(create);
            var first = (D)(await socket.Until("response.completed", root.Lifetime.Token))["response"]!;
            var tool = Output(first).Single();
            Assert.Equal("collaboration", tool["namespace"]);
            Assert.Equal("spawn_agent", tool["name"]);
            using var child = Child(root, "audit", "child-thread", ctx => Stream(ctx, Events(Terminal(Message("CHILD DONE")))));
            child.CatalogEnabled = false;
            var childResponse = await Complete(child, ClientRequest("gpt-6-luna", "audit source"));
            Assert.Contains("CHILD DONE", JsonSerializer.Serialize(childResponse));
            var next = Continue(first, SpawnResult(tool, "/root/audit"));
            next["type"] = "response.create";
            socket.ClientSend(next);
            var completed = await socket.Until("response.completed", root.Lifetime.Token);
            Assert.Contains("PARENT DONE", JsonSerializer.Serialize(completed));
            Assert.NotNull(await root.NativeStore.TryGetAsync(root.Key, "child-thread", root.Lifetime.Token));
            Assert.Null(await root.ClientStore.TryGetAsync(root.Key, "root-thread", "child-thread", root.Lifetime.Token));
            Assert.Equal("gpt-6-luna", child.FakeEndpoint.Calls.Single().Payload!["model"]);
            Assert.Equal(2, root.FakeEndpoint.Calls.Count);
        }
        finally { socket.ClientClose(); await operation; }
    }

    [Fact]
    public async Task NativeWebSocketRejectsLegacyInjectionInsteadOfStartingAServerLoop()
    {
        using var root = Root(ctx => Stream(ctx, Events(Terminal(Message("DONE")))));
        var socket = root.Socket();
        var operation = root.Service.ResponsesWebSocket();
        var create = ClientRequest();
        create["type"] = "response.create";
        socket.ClientSend(create);
        await socket.Until("response.completed", root.Lifetime.Token);
        socket.ClientSend(new D { ["type"] = "response.inject", ["response_id"] = "anything", ["input"] = new List<object?>() });
        var error = await socket.Until("error", root.Lifetime.Token);
        Assert.Contains("next response.create", JsonSerializer.Serialize(error));
        await operation;
        Assert.Single(root.FakeEndpoint.Calls);
    }

    [Fact]
    public async Task HttpSpawnAllowsBoundWebSocketChildWhoseModelHasNoCatalogFlag()
    {
        using var root = Root(ctx => Stream(ctx, Events(Terminal(Spawn("spawn_a", "audit", "child-model", "audit")))));
        await Complete(root, ClientRequest());
        using var child = Child(root, "audit", "child-thread", ctx => Stream(ctx, Events(Terminal(Message("WS CHILD DONE")))));
        child.CatalogEnabled = false;
        var socket = child.Socket();
        var operation = child.Service.ResponsesWebSocket();
        var create = ClientRequest("child-model", "audit");
        create["type"] = "response.create";
        socket.ClientSend(create);
        var completed = await socket.Until("response.completed", child.Lifetime.Token);
        Assert.Contains("WS CHILD DONE", JsonSerializer.Serialize(completed));
        Assert.Equal("child-model", child.FakeEndpoint.Calls.Single().Payload!["model"]);
        Assert.DoesNotContain(socket.Sent, item => Type(item) == "error");
        socket.ClientClose();
        await operation;
    }
}

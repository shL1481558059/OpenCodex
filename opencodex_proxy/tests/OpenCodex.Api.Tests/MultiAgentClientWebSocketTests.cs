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
    public async Task WebSocketSpawnAllowsHttpChildBeforeNativeResultInjection()
    {
        var calls = 0;
        using var root = Root(ctx => Stream(ctx, Events(Terminal(++calls == 1
            ? Spawn("spawn_a", "audit", "gpt-6-luna", "audit source") : Message("PARENT DONE")))));
        var socket = root.Socket();
        var operation = root.Service.ResponsesWebSocket();
        var create = ClientRequest();
        create["type"] = "response.create";
        socket.ClientSend(create);
        var created = await socket.Until("response.created", root.Lifetime.Token);
        var id = ((D)created["response"]!)["id"];
        D tool;
        do { tool = (D)(await socket.Until("response.output_item.done", root.Lifetime.Token))["item"]!; }
        while (Type(tool) != "function_call");
        Assert.Equal("collaboration", tool["namespace"]);
        Assert.Equal("spawn_agent", tool["name"]);
        using var child = Child(root, "audit", "child-thread", ctx => Stream(ctx, Events(Terminal(Message("CHILD DONE")))));
        child.CatalogEnabled = false;
        var childResponse = await Complete(child, ClientRequest("gpt-6-luna", "audit source"));
        Assert.Contains("CHILD DONE", JsonSerializer.Serialize(childResponse));
        Assert.DoesNotContain(socket.Sent, item => Type(item) == "response.completed");
        socket.ClientSend(new D
        {
            ["type"] = "response.inject", ["response_id"] = id,
            ["input"] = new List<object?> { SpawnResult(tool, "/root/audit") }
        });
        await socket.Until("response.inject.created", root.Lifetime.Token);
        var completed = await socket.Until("response.completed", root.Lifetime.Token);
        Assert.Contains("PARENT DONE", JsonSerializer.Serialize(completed));
        var binding = Assert.IsType<MultiAgentClientBinding>(await root.ClientStore.TryGetAsync(root.Key,
            "root-thread", "child-thread", root.Lifetime.Token));
        Assert.True(binding.SpawnCompleted);
        Assert.Equal("gpt-6-luna", child.FakeEndpoint.Calls.Single().Payload!["model"]);
        socket.ClientClose();
        await operation;
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

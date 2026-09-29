using Microsoft.AspNetCore.Mvc;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;
using static OpenCodex.Api.Tests.MultiAgentApiTestContext;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentResponseServiceTests
{
    [Fact]
    public async Task JsonUsesInternalStreamingPipelineAndReturnsFinalObject()
    {
        using var test = new MultiAgentApiTestContext(ctx => Stream(ctx, Events(Terminal(Message("DONE")))));
        var response = Assert.IsType<ObjectResult>(await test.Service.Responses(Request()));
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("completed", Assert.IsType<D>(response.Value)["status"]);
        Assert.True((bool)test.FakeEndpoint.Calls.Single().Payload!["stream"]!);
        Assert.Empty(test.Body.Text);
        Assert.Equal("test-session", test.Http.Response.Headers["X-OpenCodex-Multi-Agent-Session"]);
    }

    [Fact]
    public async Task SseWritesFirstDeltaBeforeUpstreamTerminalIsReleased()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<D> Source()
        {
            yield return new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = Message("") };
            yield return new() { ["type"] = "response.output_text.delta", ["output_index"] = 0, ["item_id"] = "up_item", ["content_index"] = 0, ["delta"] = "FIRST" };
            await release.Task;
            yield return new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = Message("FIRST") };
            yield return Terminal(Message("FIRST"));
        }
        using var test = new MultiAgentApiTestContext(ctx => Stream(ctx, Source()));
        var operation = test.Service.Responses(Request(true));
        while (!(await test.Body.Writes.Reader.ReadAsync(test.Lifetime.Token)).Contains("response.output_text.delta")) { }
        Assert.False(operation.IsCompleted);
        Assert.DoesNotContain("response.completed", test.Body.Text);
        release.SetResult();
        Assert.IsType<EmptyResult>(await operation);
        Assert.Contains("response.completed", test.Body.Text);
        Assert.Equal("text/event-stream", test.Http.Response.ContentType);
    }

    [Fact]
    public async Task DisabledRequestUsesDirectEndpointWithoutReadingBodyAgain()
    {
        using var test = new MultiAgentApiTestContext(ctx => Task.FromResult(new ProxyEndpointResult(202, new { direct = true }, false)));
        var request = Request();
        request["multi_agent"] = new D { ["enabled"] = false };
        var response = Assert.IsType<ObjectResult>(await test.Service.Responses(request));
        Assert.Equal(202, response.StatusCode);
        Assert.Single(test.FakeEndpoint.Calls);
        Assert.False(test.FakeEndpoint.Calls[0].Payload!.ContainsKey("multi_agent"));
        Assert.False((bool)test.FakeEndpoint.Calls[0].Payload!["stream"]!);
    }

    [Fact]
    public async Task PreviousResponseRequiresSameApiKeyAndSessionAndModel()
    {
        Task<ProxyEndpointResult> Handler(ProxyEndpointContext ctx) => Stream(ctx, Events(Terminal(Message("DONE"))));
        using var first = new MultiAgentApiTestContext(Handler);
        var initial = Assert.IsType<ObjectResult>(await first.Service.Responses(Request()));
        var id = (string)((D)initial.Value!)["id"]!;
        var next = Request();
        next["previous_response_id"] = id;
        next["input"] = new List<object?> { new D { ["role"] = "user", ["content"] = "next" } };
        using var same = new MultiAgentApiTestContext(Handler, first.Store, first.Key);
        Assert.IsType<ObjectResult>(await same.Service.Responses(next));
        using var otherOwner = new MultiAgentApiTestContext(Handler, first.Store);
        await Assert.ThrowsAsync<BadRequestException>(() => otherOwner.Service.Responses(next));
        using var otherSession = new MultiAgentApiTestContext(Handler, first.Store, first.Key, "another");
        await Assert.ThrowsAsync<BadRequestException>(() => otherSession.Service.Responses(next));
        using var otherModel = new MultiAgentApiTestContext(Handler, first.Store, first.Key);
        next["model"] = "another-model";
        await Assert.ThrowsAsync<BadRequestException>(() => otherModel.Service.Responses(next));
    }

    [Fact]
    public async Task MissingModelIsRejectedBeforeCallingUpstream()
    {
        using var test = new MultiAgentApiTestContext(_ => throw new Exception("must not call"));
        var request = Request();
        request.Remove("model");
        await Assert.ThrowsAsync<BadRequestException>(() => test.Service.Responses(request));
        Assert.Empty(test.FakeEndpoint.Calls);
    }
}

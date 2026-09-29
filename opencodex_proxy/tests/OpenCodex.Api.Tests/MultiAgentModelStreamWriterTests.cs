using System.Text.Json;
using System.Threading.Channels;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentModelStreamWriterTests
{
    private static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private static string Terminal(string type = "response.completed", string status = "completed", object[]? output = null) =>
        Event(new { type, response = new { id = "resp_test", status, output = output ?? [new { type = "message", content = new[] { new { type = "output_text", text = "ok" } } }], usage = new { input_tokens = 12, output_tokens = 3 } } });

    private static MultiAgentModelStreamWriter Writer(Func<Dictionary<string, object?>, CancellationToken, Task> callback) =>
        new(callback);

    [Theory]
    [InlineData("response.output_text.delta", "hello")]
    [InlineData("response.function_call_arguments.delta", "{\"name\":")]
    [InlineData("response.custom_tool_call_input.delta", "print(")]
    [InlineData("response.reasoning_summary_text.delta", "consider")]
    public async Task DeltaArrivesWhileUpstreamTerminalIsStillBlocked(string type, string delta)
    {
        var input = Channel.CreateUnbounded<string>();
        var arrived = new TaskCompletionSource<Dictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        var writer = Writer((item, _) =>
        {
            seen.Add((string)item["type"]!);
            if ((string)item["type"]! == type) arrived.TrySetResult(item);
            return Task.CompletedTask;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var operation = writer.WriteLinesAsync(input.Reader.ReadAllAsync(deadline.Token), _ => false, () => 0, deadline.Token);
        await input.Writer.WriteAsync(Event(new { type, delta }));
        var received = await arrived.Task.WaitAsync(deadline.Token);
        Assert.Equal(delta, received["delta"]);
        Assert.Null(writer.Result);
        Assert.False(operation.IsCompleted);
        Assert.DoesNotContain("response.completed", seen);
        await input.Writer.WriteAsync(Terminal());
        input.Writer.Complete();
        await operation;
        Assert.Equal(new[] { type, "response.completed" }, seen);
        Assert.Equal("completed", writer.Result!["status"]);
    }

    [Fact]
    public async Task CallbackAppliesBackpressureBeforeReadingNextEvent()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writer = Writer(async (item, token) =>
        {
            seen.Add((string)item["type"]!);
            if ((string)item["type"]! == "response.output_text.delta")
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        });
        var operation = writer.WriteLinesAsync(Chunks(Event(new { type = "response.output_text.delta", delta = "a" }) + Terminal()), _ => false, () => 0, deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        Assert.Single(seen);
        Assert.Null(writer.Result);
        release.SetResult();
        await operation;
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task ParsesCharacterFragmentsCrlfCommentsAndMultipleEventsWithoutDuplication()
    {
        var seen = new List<Dictionary<string, object?>>();
        var writer = Writer((item, _) => { seen.Add(item); return Task.CompletedTask; });
        var text = ": keepalive\r\nevent: ignored-name\r\n" +
            Event(new { type = "response.output_text.delta", delta = "中文😀" }).Replace("\n", "\r\n") +
            Terminal().Replace("\n", "\r\n") + "data: [DONE]\r\n\r\n";
        await writer.WriteLinesAsync(Chunks(text.Select(character => character.ToString()).ToArray()), _ => false, () => 0);
        Assert.Equal(2, seen.Count);
        Assert.Equal("中文😀", seen[0]["delta"]);
        Assert.Equal("response.completed", seen[1]["type"]);
        Assert.Equal(12L, Convert.ToInt64(JsonDictionaryValue.Object(writer.Result!, "usage", WebSearchPayload.DeepCopyObject)["input_tokens"]));
    }

    [Fact]
    public async Task ParsesSeveralEventsPerChunkAndUnterminatedLastEvent()
    {
        var seen = new List<string>();
        var writer = Writer((item, _) => { seen.Add((string)item["type"]!); return Task.CompletedTask; });
        await writer.WriteLinesAsync(Chunks(Event(new { type = "response.output_text.delta", delta = "a" }) +
            Event(new { type = "response.output_text.delta", delta = "b" }) + Terminal().TrimEnd('\n')), _ => false, () => 0);
        Assert.Equal(new[] { "response.output_text.delta", "response.output_text.delta", "response.completed" }, seen);
    }

    [Theory]
    [InlineData("response.incomplete", "incomplete")]
    [InlineData("response.failed", "failed")]
    public async Task PreservesUnsuccessfulTerminalStatus(string type, string status)
    {
        var seen = new List<string>();
        var writer = Writer((item, _) => { seen.Add((string)item["type"]!); return Task.CompletedTask; });
        await writer.WriteLinesAsync(Chunks(Terminal(type, status, [])), _ => false, () => 0);
        Assert.Equal(status, writer.Result!["status"]);
        Assert.Equal(new[] { type }, seen);
        Assert.Empty(JsonDictionaryValue.List(writer.Result, "output"));
    }

    [Fact]
    public async Task ErrorEventIsForwardedThenRejected()
    {
        var seen = new List<string>();
        var writer = Writer((item, _) => { seen.Add((string)item["type"]!); return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<UpstreamException>(() => writer.WriteLinesAsync(
            Chunks(Event(new { type = "error", error = new { message = "failed" } })), _ => false, () => 0));
        Assert.Contains("error event", error.Message);
        Assert.Equal(new[] { "error" }, seen);
        Assert.Null(writer.Result);
    }

    [Theory]
    [InlineData("data: {bad}\n\n", "invalid JSON")]
    [InlineData("data: []\n\n", "invalid JSON")]
    [InlineData("data: {\"type\":\"response.completed\"}\n\n", "missing its Responses")]
    [InlineData("data: [DONE]\n\n", "without a terminal")]
    public async Task RejectsInvalidOrMissingTerminal(string input, string message)
    {
        var error = await Assert.ThrowsAsync<UpstreamException>(() => new MultiAgentModelStreamWriter()
            .WriteLinesAsync(Chunks(input), _ => false, () => 0));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task CompletedWithoutOutputIsNotSuccessful()
    {
        var error = await Assert.ThrowsAsync<UpstreamException>(() => new MultiAgentModelStreamWriter()
            .WriteLinesAsync(Chunks(Terminal(output: [])), _ => false, () => 0));
        Assert.Contains("without any output", error.Message);
    }

    [Fact]
    public async Task CompletedItemsReconstructEmptyTerminalOutputInIndexOrder()
    {
        var writer = new MultiAgentModelStreamWriter();
        await writer.WriteLinesAsync(Chunks(
            Event(new { type = "response.output_item.done", output_index = 1, item = new { type = "function_call", call_id = "b", arguments = "{}" } }),
            Event(new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", call_id = "a", arguments = "{}" } }),
            Terminal(output: [])), _ => false, () => 0);
        Assert.Equal(new[] { "a", "b" }, JsonDictionaryValue.List(writer.Result!, "output")
            .Cast<Dictionary<string, object?>>().Select(item => (string)item["call_id"]!));
    }

    [Fact]
    public async Task TerminalOutputIsAuthoritativeAndCallbackMutationCannotCorruptResult()
    {
        var writer = Writer((item, _) =>
        {
            if (item["type"] as string == "response.completed")
                ((Dictionary<string, object?>)item["response"]!)["output"] = new List<object?>();
            return Task.CompletedTask;
        });
        await writer.WriteLinesAsync(Chunks(Event(new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "old" } }), Terminal()), _ => false, () => 0);
        Assert.Single(JsonDictionaryValue.List(writer.Result!, "output"));
        Assert.False(((Dictionary<string, object?>)JsonDictionaryValue.List(writer.Result!, "output")[0]!).ContainsKey("id"));
    }

    [Fact]
    public async Task CancellationReachesBlockedCallbackAndStopsBeforeTerminal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var writer = Writer(async (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var operation = writer.WriteLinesAsync(Chunks(Event(new { type = "response.output_text.delta", delta = "a" }) + Terminal()), _ => false, () => 0, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Null(writer.Result);
    }

    [Fact]
    public async Task CallbackFailureStopsUpstreamConsumption()
    {
        var writer = Writer((_, _) => throw new InvalidOperationException("consumer failed"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteLinesAsync(
            Chunks(Event(new { type = "response.output_text.delta", delta = "a" }) + Terminal()), _ => false, () => 0));
        Assert.Equal("consumer failed", error.Message);
        Assert.Null(writer.Result);
    }

    [Fact]
    public async Task KeepsFirstMatchingTtftAndSupportsNoCallback()
    {
        var writer = new MultiAgentModelStreamWriter();
        var calls = 0;
        var metrics = await writer.WriteLinesAsync(Chunks(Event(new { type = "response.created" }),
            Event(new { type = "response.output_text.delta", delta = "a" }), Terminal()),
            chunk => chunk.Contains("delta") || chunk.Contains("completed"), () => { calls++; return 17; });
        Assert.Equal(17, metrics.TtftMs);
        Assert.Equal(1, calls);
    }

    private static async IAsyncEnumerable<string> Chunks(params string[] values)
    {
        foreach (var value in values) { yield return value; await Task.Yield(); }
    }
}

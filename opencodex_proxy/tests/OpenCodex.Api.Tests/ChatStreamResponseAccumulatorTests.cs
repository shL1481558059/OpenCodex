using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ChatStreamResponseAccumulatorTests
{
    [Fact]
    public void BuildsTextReasoningRefusalAndTailUsage()
    {
        var accumulator = CreateAccumulator();

        accumulator.Accept(Event(Chunk(
            choices:
            [
                Choice(0, Delta(
                    ("role", "assistant"),
                    ("content", "Hello "),
                    ("reasoning_content", "Think "),
                    ("refusal", "No ")))
            ])));
        accumulator.Accept(Event(Chunk(
            choices:
            [
                Choice(0, Delta(
                    ("content", "world"),
                    ("reasoning_content", "carefully"),
                    ("refusal", "thanks")), "stop")
            ])));
        accumulator.Accept(Event(Chunk(
            choices: [],
            usage: Object(
                ("prompt_tokens", 11),
                ("completion_tokens", 7),
                ("total_tokens", 18)))));
        accumulator.Accept(new SseEvent("message", "[DONE]"));

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        Assert.True(accumulator.IsComplete);
        Assert.Equal("chatcmpl-test", response["id"]);
        Assert.Equal("chat.completion", response["object"]);
        Assert.Equal("gpt-test", response["model"]);

        var choice = GetOnlyChoice(response);
        var message = AssertObject(choice["message"]);
        Assert.Equal("assistant", message["role"]);
        Assert.Equal("Hello world", message["content"]);
        Assert.Equal("Think carefully", message["reasoning_content"]);
        Assert.Equal("No thanks", message["refusal"]);
        Assert.Equal("stop", choice["finish_reason"]);

        var usage = AssertObject(response["usage"]);
        Assert.Equal(11, usage["prompt_tokens"]);
        Assert.Equal(7, usage["completion_tokens"]);
        Assert.Equal(18, usage["total_tokens"]);
    }

    [Fact]
    public void ReassemblesInterleavedToolCallsByIndex()
    {
        var accumulator = CreateAccumulator();

        accumulator.Accept(Event(Chunk(choices:
        [
            Choice(0, Delta(("tool_calls", new List<object?>
            {
                ToolCall(1, id: "call_b", name: "second", arguments: "{\"b\":"),
                ToolCall(0, id: "call_a", name: "first", arguments: "{\"a\":")
            })))
        ])));
        accumulator.Accept(Event(Chunk(choices:
        [
            Choice(0, Delta(("tool_calls", new List<object?>
            {
                ToolCall(0, arguments: "1}"),
                ToolCall(1, arguments: "2}")
            })), "tool_calls")
        ])));
        accumulator.Accept(new SseEvent("message", "[DONE]"));

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        var message = AssertObject(GetOnlyChoice(response)["message"]);
        var toolCalls = AssertList(message["tool_calls"]);
        Assert.Equal(2, toolCalls.Count);

        var first = AssertObject(toolCalls[0]);
        Assert.Equal("call_a", first["id"]);
        Assert.Equal("function", first["type"]);
        var firstFunction = AssertObject(first["function"]);
        Assert.Equal("first", firstFunction["name"]);
        Assert.Equal("{\"a\":1}", firstFunction["arguments"]);

        var second = AssertObject(toolCalls[1]);
        Assert.Equal("call_b", second["id"]);
        var secondFunction = AssertObject(second["function"]);
        Assert.Equal("second", secondFunction["name"]);
        Assert.Equal("{\"b\":2}", secondFunction["arguments"]);
    }

    [Fact]
    public void KeepsMultipleChoicesSeparateAndOrdered()
    {
        var accumulator = CreateAccumulator();

        accumulator.Accept(Event(Chunk(choices:
        [
            Choice(2, Delta(("content", "third-"))),
            Choice(0, Delta(("content", "first-")))
        ])));
        accumulator.Accept(Event(Chunk(choices:
        [
            Choice(0, Delta(("content", "choice")), "stop"),
            Choice(2, Delta(("content", "choice")), "length")
        ])));
        accumulator.Accept(new SseEvent("message", "[DONE]"));

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        var choices = AssertList(response["choices"]);
        Assert.Equal(2, choices.Count);

        var first = AssertObject(choices[0]);
        Assert.Equal(0, first["index"]);
        Assert.Equal("first-choice", AssertObject(first["message"])["content"]);
        Assert.Equal("stop", first["finish_reason"]);

        var third = AssertObject(choices[1]);
        Assert.Equal(2, third["index"]);
        Assert.Equal("third-choice", AssertObject(third["message"])["content"]);
        Assert.Equal("length", third["finish_reason"]);
    }

    [Fact]
    public void PreservesErrorWithoutDoneMarker()
    {
        var accumulator = CreateAccumulator();
        var error = Object(
            ("message", "upstream failed"),
            ("type", "server_error"),
            ("code", "internal_error"));

        accumulator.Accept(Event(Object(("error", error))));

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        Assert.True(accumulator.IsComplete);
        var capturedError = AssertObject(response["error"]);
        Assert.Equal("upstream failed", capturedError["message"]);
        Assert.Equal("server_error", capturedError["type"]);
    }

    [Fact]
    public void TruncatesIncrementalTextUsingSharedBudget()
    {
        var budget = new StreamCaptureBudget(5);
        var accumulator = new ChatStreamResponseAccumulator(budget);

        accumulator.Accept(Event(Chunk(choices:
        [
            Choice(0, Delta(("content", "你好abc")), "stop")
        ])));
        accumulator.Accept(new SseEvent("message", "[DONE]"));

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());
        var content = Assert.IsType<string>(AssertObject(GetOnlyChoice(response)["message"])["content"]);
        Assert.Equal("你", content);
        Assert.True(budget.Truncated);
        Assert.True(accumulator.IsComplete);
    }

    [Fact]
    public void BoundsLargeErrorAndUsageObjects()
    {
        var budget = new StreamCaptureBudget(64);
        var accumulator = new ChatStreamResponseAccumulator(budget);
        accumulator.Accept(Event(Object(
            ("usage", Object(
                ("prompt_tokens", 3),
                ("completion_tokens", 2),
                ("details", new string('x', 200)))),
            ("error", Object(
                ("type", "server_error"),
                ("message", new string('y', 200)))))));

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());

        Assert.True(budget.Truncated);
        var usage = AssertObject(response["usage"]);
        Assert.Equal(3, usage["prompt_tokens"]);
        Assert.Equal(2, usage["completion_tokens"]);
        Assert.False(usage.ContainsKey("details"));
        AssertObject(response["error"]);
    }

    [Fact]
    public void EnforcesConfiguredCollectionLimitForLogCapture()
    {
        var budget = new StreamCaptureBudget(4096, maxCollectionItems: 1);
        var accumulator = new ChatStreamResponseAccumulator(budget);
        accumulator.Accept(Event(Chunk(choices:
        [
            Choice(0, Delta(("content", "first"))),
            Choice(1, Delta(("content", "second")))
        ])));
        accumulator.Accept(new SseEvent("message", "[DONE]"));

        var response = Assert.IsType<Dictionary<string, object?>>(accumulator.BuildResponse());

        Assert.Single(AssertList(response["choices"]));
        Assert.True(budget.Truncated);
    }

    private static ChatStreamResponseAccumulator CreateAccumulator()
        => new(new StreamCaptureBudget(1024 * 1024));

    private static SseEvent Event(Dictionary<string, object?> payload)
        => new("message", payload);

    private static Dictionary<string, object?> Chunk(
        List<object?> choices,
        Dictionary<string, object?>? usage = null)
    {
        var result = Object(
            ("id", "chatcmpl-test"),
            ("object", "chat.completion.chunk"),
            ("created", 1700000000),
            ("model", "gpt-test"),
            ("system_fingerprint", "fp-test"),
            ("service_tier", "default"),
            ("choices", choices));
        if (usage is not null)
        {
            result["usage"] = usage;
        }

        return result;
    }

    private static Dictionary<string, object?> Choice(
        int index,
        Dictionary<string, object?> delta,
        string? finishReason = null)
        => Object(
            ("index", index),
            ("delta", delta),
            ("finish_reason", finishReason));

    private static Dictionary<string, object?> Delta(
        params (string Key, object? Value)[] values)
        => Object(values);

    private static Dictionary<string, object?> ToolCall(
        int index,
        string? id = null,
        string? name = null,
        string? arguments = null)
    {
        var function = Object();
        if (name is not null)
        {
            function["name"] = name;
        }

        if (arguments is not null)
        {
            function["arguments"] = arguments;
        }

        var result = Object(
            ("index", index),
            ("type", "function"),
            ("function", function));
        if (id is not null)
        {
            result["id"] = id;
        }

        return result;
    }

    private static Dictionary<string, object?> Object(
        params (string Key, object? Value)[] values)
        => values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);

    private static Dictionary<string, object?> GetOnlyChoice(Dictionary<string, object?> response)
        => AssertObject(Assert.Single(AssertList(response["choices"])));

    private static Dictionary<string, object?> AssertObject(object? value)
        => Assert.IsType<Dictionary<string, object?>>(value);

    private static List<object?> AssertList(object? value)
        => Assert.IsType<List<object?>>(value);
}

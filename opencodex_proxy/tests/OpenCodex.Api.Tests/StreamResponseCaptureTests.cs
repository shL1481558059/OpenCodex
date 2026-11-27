using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Abstractions;
using OpenCodex.ProtocolConversion.Errors;
using System.Text.Json;
using System.Text.Encodings.Web;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class StreamResponseCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FitsCountsExactJsonBytesWithoutCreatingAuditStorage(bool strict)
    {
        var value = new Dictionary<string, object?>
        {
            ["text"] = "中😀<>&\"\\\n\ud83d",
            ["values"] = new object?[] { 1, 1.25m, true, null },
            ["typed"] = new Dictionary<string, string> { ["<key>"] = "中<value>" }
        };
        var options = new JsonSerializerOptions
        {
            Encoder = strict ? JavaScriptEncoder.UnsafeRelaxedJsonEscaping : JavaScriptEncoder.Default
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options).Length;
        using var exact = Create(bytes);
        Assert.True(exact.Fits(value));
        if (strict) Assert.Throws<UpstreamException>(() => exact.Fits(null));
        else Assert.False(exact.Fits(null));
        using var tooSmall = Create(bytes - 1);
        if (strict) Assert.Throws<UpstreamException>(() => tooSmall.Fits(value));
        else
        {
            Assert.False(tooSmall.Fits(value));
            Assert.True(tooSmall.Truncated);
        }

        StreamCaptureBudget Create(int size) => strict
            ? new StreamCaptureBudget(size, 100, 100, new NoAuditStorage(), default)
            : new StreamCaptureBudget(size);
    }

    [Fact]
    public void FitsHonorsCancellationBeforeCounting()
    {
        using var stop = new CancellationTokenSource();
        using var budget = new StreamCaptureBudget(1024, 100, 100, new NoAuditStorage(), stop.Token);
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => budget.Fits(new { text = "unused" }));
    }

    private sealed class NoAuditStorage : IProtocolAuditCapture
    {
        public int MemoryThresholdBytes => 0;
        public IProtocolAuditBuffer CreateStaging(CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Counting must not create a staging file.");
        public IProtocolAuditBody CaptureJson(object? value, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Counting must not serialize a temporary audit body.");
    }
    [Fact]
    public void ResponsesCompleted_CapturesOutputAndDropsRequestEcho()
    {
        var capture = new StreamResponseCapture(ProtocolConverter.Responses);

        capture.Accept("event: response.completed");
        capture.Accept("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"model\":\"gpt-5\",\"instructions\":\"secret\",\"tools\":[{\"name\":\"hidden\"}],\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"hello\"}]}],\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}}");
        capture.Accept("");

        var result = capture.Complete(StreamCaptureTermination.Completed);

        Assert.True(result.Completed);
        Assert.NotNull(result.Response);
        Assert.Equal("resp_1", result.Response!["id"]);
        Assert.True(result.Response.ContainsKey("output"));
        Assert.False(result.Response.ContainsKey("instructions"));
        Assert.False(result.Response.ContainsKey("tools"));
    }

    [Fact]
    public void ResponsesCompleted_ReconstructsOutputFromDoneItemsWhenTerminalOmitsIt()
    {
        var capture = new StreamResponseCapture(ProtocolConverter.Responses);

        capture.Accept("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"model\":\"gpt-5\"}}");
        capture.Accept("data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"hello\"}]}}");
        capture.Accept("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"model\":\"gpt-5\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}");

        var response = capture.Complete(StreamCaptureTermination.Completed).Response;

        Assert.NotNull(response);
        var output = Assert.IsType<List<object?>>(response!["output"]);
        var item = Assert.IsType<Dictionary<string, object?>>(Assert.Single(output));
        Assert.Equal("msg_1", item["id"]);
    }

    [Fact]
    public void ResponsesCompleted_ReconstructsOutputFromDeltasWhenTerminalAndDoneItemsOmitIt()
    {
        var capture = new StreamResponseCapture(ProtocolConverter.Responses);

        capture.Accept("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"model\":\"gpt-5\",\"status\":\"in_progress\"}}");
        capture.Accept("data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}}");
        capture.Accept("data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"hello \"}");
        capture.Accept("data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"world\"}");
        capture.Accept("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"gpt-5\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":2}}}");

        var response = capture.Complete(StreamCaptureTermination.Completed).Response;

        var output = Assert.IsType<List<object?>>(response!["output"]);
        var item = Assert.IsType<Dictionary<string, object?>>(Assert.Single(output));
        var content = Assert.IsType<List<object?>>(item["content"]);
        var part = Assert.IsType<Dictionary<string, object?>>(Assert.Single(content));
        Assert.Equal("hello world", part["text"]);
    }

    [Fact]
    public void ResponsesCompleted_ReconstructsRefusalFromDeltas()
    {
        var capture = new StreamResponseCapture(ProtocolConverter.Responses);

        capture.Accept("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"model\":\"gpt-5\",\"status\":\"in_progress\"}}");
        capture.Accept("data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}}");
        capture.Accept("data: {\"type\":\"response.refusal.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"cannot \"}");
        capture.Accept("data: {\"type\":\"response.refusal.done\",\"output_index\":0,\"content_index\":0,\"refusal\":\"cannot comply\"}");
        capture.Accept("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"gpt-5\",\"output\":[],\"usage\":{}}}");

        var response = capture.Complete(StreamCaptureTermination.Completed).Response;

        var output = Assert.IsType<List<object?>>(response!["output"]);
        var item = Assert.IsType<Dictionary<string, object?>>(Assert.Single(output));
        var content = Assert.IsType<List<object?>>(item["content"]);
        var refusal = Assert.IsType<Dictionary<string, object?>>(Assert.Single(content));
        Assert.Equal("refusal", refusal["type"]);
        Assert.Equal("cannot ", refusal["refusal"]);
    }

    [Fact]
    public void MultilineData_IsParsedWithoutChangingProtocolPayload()
    {
        var capture = new StreamResponseCapture(ProtocolConverter.Responses);

        capture.Accept("event: response.completed");
        capture.Accept("data: {\"type\":\"response.completed\",");
        capture.Accept("data: \"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"model\":\"gpt-5\",\"output\":[],\"usage\":{}}}");
        capture.Accept("");

        var result = capture.Complete(StreamCaptureTermination.Completed);

        Assert.True(result.Completed);
        Assert.Equal("resp_1", result.Response!["id"]);
    }

    [Fact]
    public void EventFieldAfterData_IsAppliedWhenBlockEnds()
    {
        var capture = new StreamResponseCapture(ProtocolConverter.Responses);

        capture.Accept("data: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"model\":\"gpt-5\",\"output\":[],\"usage\":{}}}");
        capture.Accept("event: response.completed");
        capture.Accept("");

        var result = capture.Complete(StreamCaptureTermination.Completed);

        Assert.True(result.Completed);
        Assert.Equal("resp_1", result.Response!["id"]);
    }

    [Fact]
    public void MalformedAndInterruptedStream_IsMarkedIncomplete()
    {
        var capture = new StreamResponseCapture(ProtocolConverter.Responses);
        capture.Accept("data: not-json");
        capture.Accept("");
        capture.Accept("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"model\":\"gpt-5\"}}");

        var result = capture.Complete(StreamCaptureTermination.UpstreamError);

        Assert.False(result.Completed);
        Assert.Equal(1, result.MalformedEventCount);
        var metadata = Assert.IsType<Dictionary<string, object?>>(result.Response!["_opencodex_capture"]);
        Assert.Equal(false, metadata["completed"]);
        Assert.Equal("UpstreamError", metadata["termination"]);
    }

    [Fact]
    public void CancellationBeforeFirstEnvelope_StillProducesCaptureMetadata()
    {
        var capture = new StreamResponseCapture(ProtocolConverter.Responses);

        var result = capture.Complete(StreamCaptureTermination.ClientCancelled);

        Assert.NotNull(result.Response);
        var metadata = Assert.IsType<Dictionary<string, object?>>(result.Response!["_opencodex_capture"]);
        Assert.Equal(false, metadata["completed"]);
        Assert.Equal("ClientCancelled", metadata["termination"]);
    }

    [Fact]
    public void OversizedResponsesOutput_IsDroppedAndMarkedTruncated()
    {
        var capture = new StreamResponseCapture(ProtocolConverter.Responses, maxCapturedBytes: 32);
        capture.Accept($"data: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"resp_1\",\"status\":\"completed\",\"model\":\"gpt-5\",\"output\":[{{\"type\":\"message\",\"text\":\"{new string('x', 200)}\"}}],\"usage\":{{}}}}}}");

        var result = capture.Complete(StreamCaptureTermination.Completed);

        Assert.True(result.Truncated);
        Assert.NotNull(result.Response);
        Assert.False(result.Response!.ContainsKey("output"));
        var metadata = Assert.IsType<Dictionary<string, object?>>(result.Response["_opencodex_capture"]);
        Assert.Equal(true, metadata["truncated"]);
    }

    [Fact]
    public void Utf8Budget_DoesNotSplitSurrogatePairs()
    {
        var budget = new StreamCaptureBudget(5);
        var target = new System.Text.StringBuilder();

        budget.Append(target, "😀ab");

        Assert.Equal("😀a", target.ToString());
        Assert.True(budget.Truncated);
    }

    [Fact]
    public void OversizedPendingSseData_IsDiscardedUntilNextBoundary()
    {
        var capture = new StreamResponseCapture(ProtocolConverter.Responses);
        capture.Accept($"data: {new string('x', 300 * 1024)}");
        capture.Accept("data: ignored-while-discarding");
        capture.Accept("");
        capture.Accept("event: response.completed");
        capture.Accept("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"model\":\"gpt-5\",\"output\":[],\"usage\":{}}}");
        capture.Accept("");

        var result = capture.Complete(StreamCaptureTermination.Completed);

        Assert.Equal("resp_1", result.Response!["id"]);
        Assert.True(result.Truncated);
        Assert.True(result.MalformedEventCount > 0);
        Assert.IsType<Dictionary<string, object?>>(result.Response["_opencodex_capture"]);
    }
}

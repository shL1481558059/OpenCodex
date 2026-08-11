using System.Diagnostics;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyStreamServiceTests
{
    [Fact]
    public async Task StreamAsync_ConvertedMessages_StreamsReasoningAndUsesItForTtft()
    {
        var upstream = new SequencedUpstreamClient(
        [
            ("event: message_start", 0),
            ("data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-3\",\"usage\":{\"input_tokens\":10,\"output_tokens\":0}}}", 0),
            ("", 0),
            ("event: content_block_start", 0),
            ("data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}", 0),
            ("", 0),
            ("event: content_block_delta", 0),
            ("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"I should inspect the logs first.\"}}", 0),
            ("", 80),
            ("event: content_block_start", 0),
            ("data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"exec_command\",\"input\":{}}}", 0),
            ("", 0),
            ("event: content_block_delta", 0),
            ("data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"cmd\\\":\\\"pwd\\\"}\"}}", 0),
            ("", 0),
            ("event: content_block_stop", 0),
            ("data: {\"type\":\"content_block_stop\",\"index\":1}", 0),
            ("", 0),
            ("event: message_delta", 0),
            ("data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":30}}", 0),
            ("", 0),
            ("event: message_stop", 0),
            ("data: {\"type\":\"message_stop\"}", 0),
            ("", 0)
        ]);
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(upstream, logs, new StubWebSearchSimulator(false, []));
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "messages",
            ["type"] = ProtocolConverter.Messages
        };
        var route = new ProxyRouteDto(
            channel,
            "public-model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: true);
        var started = Stopwatch.GetTimestamp();
        var context = new ProxyStreamContext(
            started,
            requestLogId: Guid.NewGuid(),
            requestId: "req_reasoning",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: new Dictionary<string, object?>(),
            payload: new Dictionary<string, object?>(),
            upstreamRequest: new Dictionary<string, object?>(),
            entryProtocol: ProtocolConverter.Responses,
            route: route,
            channelType: ProtocolConverter.Messages,
            channelId: "messages",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None);

        await service.StreamAsync(context);

        Assert.Contains(writer.Lines, line => line.Contains("response.reasoning_summary_text.delta", StringComparison.Ordinal));
        Assert.Contains(writer.Lines, line => line.Contains("response.function_call_arguments.delta", StringComparison.Ordinal));
        Assert.NotNull(logs.LastContext);
        Assert.True(logs.LastContext!.DurationMs >= 50);
        Assert.True(
            logs.LastContext.TtftMs <= logs.LastContext.DurationMs - 40,
            $"expected TTFT to use early reasoning delta, got ttft={logs.LastContext.TtftMs}, duration={logs.LastContext.DurationMs}");
    }

    [Fact]
    public async Task StreamAsync_MessagesWebSearchSimulation_UsesSimulatorBranch()
    {
        var upstream = new ThrowingUpstreamClient();
        var logs = new StubProxyLogService();
        var webSearch = new StubWebSearchSimulator(
            canSimulate: true,
            streamLines:
            [
                "event: response.created\ndata: {\"type\":\"response.created\"}\n\n",
                "event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n"
            ]);
        var service = new ProxyStreamService(upstream, logs, webSearch);
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "messages",
            ["type"] = ProtocolConverter.Messages
        };
        var route = new ProxyRouteDto(
            channel,
            "public-model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: true);
        var context = new ProxyStreamContext(
            startedTimestamp: 0,
            requestLogId: Guid.NewGuid(),
            requestId: "req_1",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: new Dictionary<string, object?>(),
            payload: new Dictionary<string, object?>
            {
                ["tools"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "web_search" } }
            },
            upstreamRequest: new Dictionary<string, object?>(),
            entryProtocol: ProtocolConverter.Responses,
            route: route,
            channelType: ProtocolConverter.Messages,
            channelId: "messages",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None);

        await service.StreamAsync(context);

        Assert.True(webSearch.StreamCalled);
        Assert.True(writer.Prepared);
        Assert.Equal(3, writer.Lines.Count);
        Assert.Contains("response.created", writer.Lines[0], StringComparison.Ordinal);
        Assert.Contains("response.completed", writer.Lines[1], StringComparison.Ordinal);
        Assert.Equal("data: [DONE]\n\n", writer.Lines[2]);
        Assert.NotNull(logs.LastContext?.StreamLines);
        Assert.Contains(logs.LastContext!.StreamLines!, line =>
            line.Source == "upstream"
            && line.RawLine.Contains("content_block_delta", StringComparison.Ordinal));
        Assert.Contains(logs.LastContext!.StreamLines!, line =>
            line.Source == "downstream"
            && line.RawLine.Contains("response.completed", StringComparison.Ordinal));
        Assert.Contains(logs.LastContext.StreamLines!, line =>
            line.Source == "downstream"
            && line.RawLine == "data: [DONE]");
    }

    [Fact]
    public async Task StreamAsync_UpstreamFailure_LogsRealStatusCodeAndUpstreamBody()
    {
        var upstream = new FailingUpstreamClient(new UpstreamException(
            "upstream returned HTTP 429",
            ProxyHttpStatus.TooManyRequests,
            body: new Dictionary<string, object?>
            {
                ["error"] = new Dictionary<string, object?>
                {
                    ["message"] = "rate limit exceeded",
                    ["type"] = "rate_limit"
                }
            },
            channelId: "responses"));
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(upstream, logs, new StubWebSearchSimulator(false, []));
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "responses",
            ["type"] = ProtocolConverter.Responses
        };
        var route = new ProxyRouteDto(
            channel,
            "public-model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: true);
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req_stream_fail",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: new Dictionary<string, object?>(),
            payload: new Dictionary<string, object?>(),
            upstreamRequest: new Dictionary<string, object?>(),
            entryProtocol: ProtocolConverter.Responses,
            route: route,
            channelType: ProtocolConverter.Responses,
            channelId: "responses",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None);

        var exception = await Assert.ThrowsAsync<UpstreamException>(() => service.StreamAsync(context));

        Assert.Equal(429, exception.StatusCode);
        Assert.NotNull(logs.LastContext);
        Assert.Equal(429, logs.LastContext!.StatusCode);
        Assert.Equal("upstream returned HTTP 429", logs.LastContext.Error);
        Assert.NotNull(logs.LastContext.UpstreamResponse);
        var upstreamError = Assert.IsType<Dictionary<string, object?>>(logs.LastContext.UpstreamResponse!["error"]);
        var upstreamDetail = Assert.IsType<Dictionary<string, object?>>(upstreamError["error"]);
       Assert.Equal("rate limit exceeded", upstreamDetail["message"]);
   }

    [Fact]
    public async Task StreamAsync_UpstreamFailure_DoesNotPrepareSseBeforeUpstreamReturns()
    {
        var upstream = new FailingUpstreamClient(new UpstreamException(
            "upstream returned HTTP 503",
            ProxyHttpStatus.ServiceUnavailable,
            channelId: "responses"));
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(upstream, logs, new StubWebSearchSimulator(false, []));
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "responses",
            ["type"] = ProtocolConverter.Responses
        };
        var route = new ProxyRouteDto(
            channel,
            "public-model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: true);
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req_no_prepare",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: new Dictionary<string, object?>(),
            payload: new Dictionary<string, object?>(),
            upstreamRequest: new Dictionary<string, object?>(),
            entryProtocol: ProtocolConverter.Responses,
            route: route,
            channelType: ProtocolConverter.Responses,
            channelId: "responses",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None);

        await Assert.ThrowsAsync<UpstreamException>(() => service.StreamAsync(context));

        Assert.False(writer.Prepared);
        Assert.Empty(writer.Lines);
    }

    [Fact]
    public async Task StreamAsync_ConvertedUpstreamFailure_DoesNotPrepareSseBeforeUpstreamReturns()
    {
        var upstream = new FailingUpstreamClient(new UpstreamException(
            "upstream returned HTTP 406",
            406,
            channelId: "chat"));
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(upstream, logs, new StubWebSearchSimulator(false, []));
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "chat",
            ["type"] = ProtocolConverter.Chat
        };
        var route = new ProxyRouteDto(
            channel,
            "public-model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: true);
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req_converted_no_prepare",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: new Dictionary<string, object?>(),
            payload: new Dictionary<string, object?>(),
            upstreamRequest: new Dictionary<string, object?>(),
            entryProtocol: ProtocolConverter.Responses,
            route: route,
            channelType: ProtocolConverter.Chat,
            channelId: "chat",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None);

        var exception = await Assert.ThrowsAsync<UpstreamException>(() => service.StreamAsync(context));

        Assert.Equal(406, exception.StatusCode);
        Assert.False(writer.Prepared);
        Assert.Empty(writer.Lines);
    }

    [Fact]
    public async Task StreamAsync_PassThroughSuccess_PrepareSseDeferredUntilFirstLine()
    {
        var upstream = new SequencedUpstreamClient(
        [
            ("data: {\"type\":\"response.created\"}", 0),
            ("", 0),
            ("data: [DONE]", 0)
        ]);
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(upstream, logs, new StubWebSearchSimulator(false, []));
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "responses",
            ["type"] = ProtocolConverter.Responses
        };
        var route = new ProxyRouteDto(
            channel,
            "public-model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: true);
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req_deferred",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: new Dictionary<string, object?>(),
            payload: new Dictionary<string, object?>(),
            upstreamRequest: new Dictionary<string, object?>(),
            entryProtocol: ProtocolConverter.Responses,
            route: route,
            channelType: ProtocolConverter.Responses,
            channelId: "responses",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None);

        await service.StreamAsync(context);

        Assert.True(writer.Prepared);
        Assert.Equal(3, writer.Lines.Count);
    }

    [Fact]
    public async Task StreamAsync_PassThrough_CapturesOriginalUpstreamSseLines()
    {
        var upstream = new SequencedUpstreamClient(
        [
            ("event: response.output_text.delta", 0),
            ("data: {\"delta\":\"hello\"}", 0),
            ("", 0),
            ("data: [DONE]", 0)
        ]);
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(upstream, logs, new StubWebSearchSimulator(false, []));
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "responses",
            ["type"] = ProtocolConverter.Responses
        };
        var route = new ProxyRouteDto(
            channel,
            "public-model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: true);
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req-stream-lines",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: new Dictionary<string, object?>(),
            payload: new Dictionary<string, object?>(),
            upstreamRequest: new Dictionary<string, object?>(),
            entryProtocol: ProtocolConverter.Responses,
            route: route,
            channelType: ProtocolConverter.Responses,
            channelId: "responses",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None);

        await service.StreamAsync(context);

        Assert.NotNull(logs.LastContext);
        Assert.NotNull(logs.LastContext!.StreamLines);
        Assert.Collection(
            logs.LastContext.StreamLines!,
            line =>
            {
                Assert.Equal(0, line.Sequence);
                Assert.Equal("upstream", line.Source);
                Assert.Equal("event: response.output_text.delta", line.RawLine);
            },
            line =>
            {
                Assert.Equal(1, line.Sequence);
                Assert.Equal("data: {\"delta\":\"hello\"}", line.RawLine);
            },
            line =>
            {
                Assert.Equal(2, line.Sequence);
                Assert.Equal(string.Empty, line.RawLine);
            },
            line =>
            {
                Assert.Equal(3, line.Sequence);
                Assert.Equal("data: [DONE]", line.RawLine);
            });
    }

    [Fact]
    public async Task StreamAsync_PassThrough_LogsCompleteResponsesPayload()
    {
        var upstreamLines = new[]
        {
            "event: response.completed",
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"upstream-model\",\"instructions\":\"secret instructions\",\"tools\":[{\"name\":\"secret_tool\"}],\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"hello\"}]}],\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}}",
            "",
            "data: [DONE]"
        };
        var upstream = new SequencedUpstreamClient(upstreamLines.Select(line => (line, 0)).ToArray());
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(upstream, logs, new StubWebSearchSimulator(false, []));
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "responses",
            ["type"] = ProtocolConverter.Responses
        };
        var route = new ProxyRouteDto(
            channel,
            "public-model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: true);
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req-complete-response",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: new Dictionary<string, object?>(),
            payload: new Dictionary<string, object?>(),
            upstreamRequest: new Dictionary<string, object?>(),
            entryProtocol: ProtocolConverter.Responses,
            route: route,
            channelType: ProtocolConverter.Responses,
            channelId: "responses",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None);

        await service.StreamAsync(context);

        Assert.Equal(
            upstreamLines[..3].Append("data: [DONE]\n\n"),
            writer.Lines);
        Assert.NotNull(logs.LastContext?.UpstreamResponse);
        var response = logs.LastContext!.UpstreamResponse!;
        Assert.Equal("resp-1", response["id"]);
        Assert.True(response.ContainsKey("output"));
        Assert.False(response.ContainsKey("instructions"));
        Assert.False(response.ContainsKey("tools"));
    }

    [Fact]
    public async Task StreamAsync_PassThrough_RecordsFinalDoneWrittenToClient()
    {
        var upstream = new SequencedUpstreamClient(
        [
            ("event: response.completed", 0),
            ("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-done\",\"status\":\"completed\"}}", 0),
            ("", 0),
            ("data: [DONE]", 0)
        ]);
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(upstream, logs, new StubWebSearchSimulator(false, []));
        var writer = new CapturingProxyStreamWriter();
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req-final-done",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: new Dictionary<string, object?>(),
            payload: new Dictionary<string, object?>(),
            upstreamRequest: new Dictionary<string, object?>(),
            entryProtocol: ProtocolConverter.Responses,
            route: new ProxyRouteDto(
                new Dictionary<string, object?> { ["id"] = "responses", ["type"] = ProtocolConverter.Responses },
                "public-model",
                "upstream-model",
                supportsImage: false,
                matchedModelMapping: true),
            channelType: ProtocolConverter.Responses,
            channelId: "responses",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None);

        await service.StreamAsync(context);

        Assert.Equal("data: [DONE]\n\n", writer.Lines.Last());
        Assert.NotNull(logs.LastContext?.StreamLines);
        Assert.Contains(logs.LastContext!.StreamLines!, line =>
            line.Source == "downstream" && line.RawLine == "data: [DONE]");
    }

    [Fact]
    public async Task StreamAsync_ConvertedChat_CapturesUpstreamAndDownstreamDeltas()
    {
        var upstream = new SequencedUpstreamClient(
        [
            ("data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}", 0),
            ("", 0),
            ("data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1,\"total_tokens\":6}}", 0),
            ("", 0)
        ]);
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(upstream, logs, new StubWebSearchSimulator(false, []));
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "chat",
            ["type"] = ProtocolConverter.Chat
        };
        var route = new ProxyRouteDto(
            channel,
            "public-model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: true);
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req-converted-stream-lines",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: new Dictionary<string, object?>(),
            payload: new Dictionary<string, object?>(),
            upstreamRequest: new Dictionary<string, object?>(),
            entryProtocol: ProtocolConverter.Responses,
            route: route,
            channelType: ProtocolConverter.Chat,
            channelId: "chat",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None);

        await service.StreamAsync(context);

        Assert.Contains(writer.Lines, line => line.Contains("response.output_text.delta", StringComparison.Ordinal));
        Assert.NotNull(logs.LastContext?.StreamLines);
        var streamLines = logs.LastContext!.StreamLines!;
        Assert.Contains(streamLines, line =>
            line.Source == "upstream"
            && line.RawLine.Contains("\"choices\"", StringComparison.Ordinal)
            && line.RawLine.Contains("\"content\":\"hello\"", StringComparison.Ordinal));
        Assert.Contains(streamLines, line =>
            line.Source == "downstream"
            && line.RawLine == "event: response.output_text.delta");
        Assert.Contains(streamLines, line =>
            line.Source == "downstream"
            && line.RawLine.Contains("\"type\":\"response.output_text.delta\"", StringComparison.Ordinal)
            && line.RawLine.Contains("\"delta\":\"hello\"", StringComparison.Ordinal));
        Assert.Contains(streamLines, line =>
            line.Source == "downstream"
            && line.RawLine.Contains("response.created", StringComparison.Ordinal));
        Assert.Contains(streamLines, line =>
            line.Source == "downstream"
            && line.RawLine.Contains("response.in_progress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureStreamLines_PreservesAllEventsAndPayloads()
    {
        var input = new[]
        {
            "event: response.created",
            "data: {\"type\":\"response.created\",\"response\":{\"model\":\"gpt-5\",\"instructions\":\"secret instructions\",\"tools\":[{\"name\":\"secret_tool\"}]}}",
            "",
            "event: response.output_text.delta",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}",
            "",
            "event: response.completed",
            "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"gpt-5\",\"status\":\"completed\",\"instructions\":\"secret instructions\",\"tools\":[{\"name\":\"secret_tool\"}],\"output\":[{\"type\":\"message\"}],\"usage\":{\"input_tokens\":1,\"output_tokens\":2}}}",
            ""
        };
        var capture = new List<ProxyRequestStreamLineCapture>();
        var forwarded = new List<string>();

        await foreach (var line in ProxyStreamService.CaptureStreamLines(
            ToAsyncEnumerable(input),
            capture,
            "upstream",
            CancellationToken.None))
        {
            forwarded.Add(line);
        }

        Assert.Equal(input, forwarded);
        Assert.Equal(input, capture.Select(line => line.RawLine));
        Assert.Contains(capture, line => line.RawLine.Contains("secret instructions", StringComparison.Ordinal));
        Assert.Contains(capture, line => line.RawLine.Contains("secret_tool", StringComparison.Ordinal));
        Assert.Contains(capture, line => line.RawLine.Contains("response.created", StringComparison.Ordinal));
        Assert.Contains(capture, line => line.RawLine == "event: response.output_text.delta");
        Assert.Contains(capture, line => line.RawLine.Contains("\"delta\":\"hello\"", StringComparison.Ordinal));
        var completed = Assert.Single(capture, line =>
            line.RawLine.StartsWith("data:", StringComparison.Ordinal)
            && line.RawLine.Contains("\"type\":\"response.completed\"", StringComparison.Ordinal));
        Assert.Contains("\"usage\"", completed.RawLine, StringComparison.Ordinal);
        Assert.Contains("\"instructions\"", completed.RawLine, StringComparison.Ordinal);
        Assert.Contains("\"tools\"", completed.RawLine, StringComparison.Ordinal);
        Assert.Contains("\"output\":[", completed.RawLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturePassThroughResponse_ForwardsAllLines()
    {
        var input = new[] { "event: message", "data: {}", "", "data: [DONE]" };

        var (result, _) = await CapturePassThroughAsync(ProtocolConverter.Chat, input);

        Assert.Equal(input, result);
    }

    [Fact]
    public async Task CapturePassThroughResponse_ExtractsChatResponseAndUsage()
    {
        var lines = new[]
        {
            "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o\",\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}",
            "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o\",\"choices\":[{\"delta\":{}}],\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":30,\"total_tokens\":80}}",
            "data: [DONE]"
        };

        var (_, response) = await CapturePassThroughAsync(ProtocolConverter.Chat, lines);

        Assert.NotNull(response);
        Assert.Equal("gpt-4o", response!["model"]);
        var usage = Assert.IsType<Dictionary<string, object?>>(response["usage"]);
        Assert.Equal(50, Convert.ToInt32(usage["prompt_tokens"]));
        Assert.Equal(30, Convert.ToInt32(usage["completion_tokens"]));
    }

    [Fact]
    public async Task CapturePassThroughResponse_ExtractsResponsesModelAndUsage()
    {
        var lines = new[]
        {
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp-1\",\"model\":\"gpt-4o\",\"status\":\"in_progress\"}}",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}",
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\",\"model\":\"gpt-4o\",\"status\":\"completed\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50,\"total_tokens\":150}}}",
            "data: [DONE]"
        };

        var (_, response) = await CapturePassThroughAsync(ProtocolConverter.Responses, lines);

        Assert.NotNull(response);
        Assert.Equal("gpt-4o", response!["model"]);
        var usage = Assert.IsType<Dictionary<string, object?>>(response["usage"]);
        Assert.Equal(100, Convert.ToInt32(usage["input_tokens"]));
        Assert.Equal(50, Convert.ToInt32(usage["output_tokens"]));
    }

    [Fact]
    public async Task CapturePassThroughResponse_ResponsesCompletedPreservesOutputAndExcludesRequestEcho()
    {
        var lines = new[]
        {
            "event: response.completed",
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\",\"object\":\"response\",\"model\":\"gpt-4o\",\"status\":\"completed\",\"instructions\":\"secret instructions\",\"tools\":[{\"name\":\"secret_tool\"}],\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"hello\"}]}],\"usage\":{\"input_tokens\":100,\"output_tokens\":50,\"total_tokens\":150}}}",
            "",
            "data: [DONE]"
        };

        var (forwarded, response) = await CapturePassThroughAsync(ProtocolConverter.Responses, lines);

        Assert.Equal(lines[..3], forwarded);
        Assert.NotNull(response);
        Assert.Equal("resp-1", response!["id"]);
        Assert.True(response.ContainsKey("output"));
        Assert.False(response.ContainsKey("instructions"));
        Assert.False(response.ContainsKey("tools"));
    }

    [Fact]
    public async Task CapturePassThroughResponse_StopsAfterResponsesTerminalEventWhenUpstreamStaysOpen()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var capture = new StreamResponseCapture(ProtocolConverter.Responses);
        var forwarded = new List<string>();

        await foreach (var line in ProxyStreamService.CapturePassThroughResponse(
            CompletedResponseThenWait(cancellation.Token),
            capture,
            cancellation.Token))
        {
            forwarded.Add(line);
        }

        Assert.Equal(3, forwarded.Count);
        Assert.Equal("event: response.completed", forwarded[0]);
        Assert.StartsWith("data: {\"type\":\"response.completed\"", forwarded[1], StringComparison.Ordinal);
        Assert.Equal(string.Empty, forwarded[2]);
        var response = capture.Complete(StreamCaptureTermination.Completed).Response;
        Assert.NotNull(response);
        Assert.Equal("resp-terminal", response!["id"]);
    }

    [Fact]
    public async Task CapturePassThroughResponse_ExtractsMessagesResponseAndMergedUsage()
    {
        var lines = new[]
        {
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-1\",\"model\":\"claude-sonnet\",\"type\":\"message\",\"role\":\"assistant\",\"usage\":{\"input_tokens\":200,\"output_tokens\":0}}}",
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":80}}",
            "data: [DONE]"
        };

        var (_, response) = await CapturePassThroughAsync(ProtocolConverter.Messages, lines);

        Assert.NotNull(response);
        Assert.Equal("claude-sonnet", response!["model"]);
        var usage = Assert.IsType<Dictionary<string, object?>>(response["usage"]);
        Assert.Equal(200, Convert.ToInt32(usage["input_tokens"]));
        Assert.Equal(80, Convert.ToInt32(usage["output_tokens"]));
    }

    [Fact]
    public async Task CapturePassThroughResponse_MarksMissingEnvelopeAsIncomplete()
    {
        var lines = new[]
        {
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}",
            "data: [DONE]"
        };

        var (_, response) = await CapturePassThroughAsync(ProtocolConverter.Responses, lines);

        Assert.NotNull(response);
        var metadata = Assert.IsType<Dictionary<string, object?>>(response!["_opencodex_capture"]);
        Assert.Equal(false, metadata["completed"]);
    }

    [Fact]
    public async Task CapturePassThroughResponse_HandlesNonJsonDataLines()
    {
        var lines = new[]
        {
            "data: not-json",
            "data: {\"model\":\"gpt-4o\",\"usage\":{\"input_tokens\":1,\"output_tokens\":2}}"
        };

        var (_, response) = await CapturePassThroughAsync("unknown", lines);

        Assert.NotNull(response);
        Assert.Equal("gpt-4o", response!["model"]);
    }

    [Fact]
    public async Task CapturePassThroughResponse_UsageOnlyFallbackUsesLatestUsage()
    {
        // A SSE chunk that has usage at both top-level and inside nested object.
        // Top-level usage should be used (first-wins, the model key is also in both places).
        var lines = new[]
        {
            "data: {\"model\":\"top-model\",\"usage\":{\"input_tokens\":300},\"response\":{\"model\":\"nested-model\",\"usage\":{\"input_tokens\":999}}}"
        };

        var (_, response) = await CapturePassThroughAsync("unknown", lines);

        Assert.NotNull(response);
        Assert.Equal("top-model", response!["model"]);
        var usage = Assert.IsType<Dictionary<string, object?>>(response["usage"]);
        Assert.Equal(999, Convert.ToInt32(usage["input_tokens"]));
    }

    [Fact]
    public async Task CapturePassThroughResponse_EmptyStreamIsMarkedIncomplete()
    {
        var (_, response) = await CapturePassThroughAsync(ProtocolConverter.Responses, []);

        Assert.NotNull(response);
        var metadata = Assert.IsType<Dictionary<string, object?>>(response!["_opencodex_capture"]);
        Assert.Equal(false, metadata["completed"]);
    }

    [Fact]
    public async Task CapturePassThroughResponse_ModelWithoutUsageProducesResponse()
    {
        // response.created provides model but no usage at all.
        var lines = new[]
        {
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"model\":\"gpt-mini\"}}",
            "data: [DONE]"
        };

        var (_, response) = await CapturePassThroughAsync(ProtocolConverter.Responses, lines);

        Assert.NotNull(response);
        Assert.Equal("gpt-mini", response!["model"]);
        var usage = Assert.IsType<Dictionary<string, object?>>(response["usage"]);
        Assert.Empty(usage);
    }

    private static async Task<(List<string> Forwarded, Dictionary<string, object?>? Response)>
        CapturePassThroughAsync(string protocol, IEnumerable<string> lines)
    {
        var capture = new StreamResponseCapture(protocol);
        var forwarded = new List<string>();
        await foreach (var line in ProxyStreamService.CapturePassThroughResponse(
            ToAsyncEnumerable(lines), capture, CancellationToken.None))
        {
            forwarded.Add(line);
        }

        return (
            forwarded,
            capture.Complete(StreamCaptureTermination.Completed).Response);
    }

    private static async IAsyncEnumerable<string> ToAsyncEnumerable(
        IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<string> CompletedResponseThenWait(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return "event: response.completed";
        yield return "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-terminal\",\"model\":\"gpt-5\",\"status\":\"completed\"}}";
        yield return string.Empty;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class CapturingProxyStreamWriter : IProxyStreamWriter
    {
        public bool Prepared { get; private set; }

        public List<string> Lines { get; } = [];

        public void PrepareSse()
        {
            Prepared = true;
        }

        public async Task<StreamWriteMetrics> WriteLinesAsync(
            IAsyncEnumerable<string> lines,
            Func<string, bool> countsForTtft,
            Func<int> elapsedMilliseconds,
            CancellationToken cancellationToken = default)
        {
            var metrics = new StreamWriteMetrics();
            await foreach (var line in lines.WithCancellation(cancellationToken))
            {
                if (!Prepared)
                {
                    PrepareSse();
                }

                Lines.Add(line);
                if (metrics.TtftMs is null && countsForTtft(line))
                {
                    metrics.TtftMs = elapsedMilliseconds();
                }
            }

            return metrics;
        }
    }

    private sealed class ThrowingUpstreamClient : IUpstreamClient
    {
        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("should not use direct upstream post in simulator branch");
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new NotSupportedException("should not use direct upstream stream in simulator branch");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class FailingUpstreamClient : IUpstreamClient
    {
        private readonly Exception _exception;

        public FailingUpstreamClient(Exception exception)
        {
            _exception = exception;
        }

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("non-stream path is not used in this test");
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw _exception;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class SequencedUpstreamClient : IUpstreamClient
    {
        private readonly IReadOnlyList<(string Line, int DelayAfterMs)> _lines;

        public SequencedUpstreamClient(IReadOnlyList<(string Line, int DelayAfterMs)> lines)
        {
            _lines = lines;
        }

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("non-stream path is not used in this test");
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var (line, delayAfterMs) in _lines)
            {
                yield return line;
                if (delayAfterMs > 0)
                {
                    await Task.Delay(delayAfterMs, cancellationToken);
                }
                else
                {
                    await Task.Yield();
                }
            }
        }
    }

    private sealed class StubWebSearchSimulator : IWebSearchSimulator
    {
        private readonly bool _canSimulate;
        private readonly IReadOnlyList<string> _streamLines;

        public StubWebSearchSimulator(bool canSimulate, IReadOnlyList<string> streamLines)
        {
            _canSimulate = canSimulate;
            _streamLines = streamLines;
        }

        public bool StreamCalled { get; private set; }

        public string CurrentMode()
        {
            return _canSimulate ? WebSearchModes.Simulate : WebSearchModes.Convert;
        }

        public bool CanSimulate(
            string entryProtocol,
            string channelType,
            string ownerRole,
            IReadOnlyDictionary<string, object?> payload)
        {
            return _canSimulate;
        }

        public Task<WebSearchSimulationResult> RunAsync(
            IReadOnlyDictionary<string, object?> channel,
            Dictionary<string, object?> upstreamRequest,
            Dictionary<string, object?> payload,
            string? originalModel,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("non-stream path is not used in this test");
        }

        public async IAsyncEnumerable<string> RunChatStreamAsync(
            IReadOnlyDictionary<string, object?> channel,
            Dictionary<string, object?> upstreamRequest,
            Dictionary<string, object?> payload,
            string? originalModel,
            int defaultTimeout,
            WebSearchStreamResult result,
            Func<IAsyncEnumerable<string>, string, IAsyncEnumerable<string>>? streamCapture,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamCalled = true;
            result.ResponsePayload = new Dictionary<string, object?>();
            if (streamCapture is not null)
            {
                await foreach (var _ in streamCapture(
                    ToAsyncEnumerable([
                        "event: content_block_delta",
                        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"upstream text\"}}",
                        ""]),
                    "upstream").WithCancellation(cancellationToken))
                {
                }
            }

            foreach (var line in _streamLines)
            {
                yield return line;
                await Task.Yield();
            }
        }
    }

    private sealed class StubProxyLogService : IProxyLogService
    {
        public Guid CreateQueuedLog(ProxyRequestLogQueuedContext context)
        {
            return Guid.NewGuid();
        }

        public void MarkProcessing(Guid requestLogId, ProxyRequestLogProcessingContext context)
        {
        }

        public Task CompleteLogAsync(Guid requestLogId, ProxyLogContext context, ProxyRequestMetadata request)
        {
            LastContext = context;
            return Task.CompletedTask;
        }

        public ProxyLogContext? LastContext { get; private set; }

        public Task<Guid> WriteLogAsync(ProxyLogContext context, ProxyRequestMetadata request)
        {
            LastContext = context;
            return Task.FromResult(Guid.Empty);
        }

        public Task<Guid> WriteLogAsync(ProxyRequestLogContext context)
        {
            return Task.FromResult(Guid.Empty);
        }
    }
}

using System.Diagnostics;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.Core.Services.WebSearch;
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
    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 499)]
    public async Task StreamAsync_PhysicalWriter_LogsWhetherSuccessfulTerminalWasFlushed(bool failFlush, int expectedStatus)
    {
        using var cancellation = new CancellationTokenSource();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Response.Body = new TerminalDisconnectStream(cancellation, failFlush);
        var logs = new StubProxyLogService();
        var payload = new Dictionary<string, object?>();
        var channel = new Dictionary<string, object?> { ["id"] = "test", ["type"] = "responses" };
        var context = new ProxyStreamContext(
            Stopwatch.GetTimestamp(), Guid.NewGuid(), "terminal-disconnect", "admin", null, payload, payload,
            new Dictionary<string, object?>(), "responses", new ProxyRouteDto(channel, "public", "model", false, true),
            "responses", "test", "superadmin", "model", "public", 120,
            new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            new OpenCodex.Api.Infrastructure.ProxyStreamResponseWriter(http.Response), cancellation.Token);
        var operation = new ProxyStreamService(new SequencedUpstreamClient(
        [
            ("data: {\"type\":\"response.created\",\"response\":{\"id\":\"r1\",\"status\":\"in_progress\"}}", 0), ("", 0),
            ("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"status\":\"completed\",\"output\":[]}}", 0), ("", 0),
            ("data: [DONE]", 0), ("", 0)
        ]), logs, new StubWebSearchToolExecutor(), WebSearchTestStore.Create()).StreamAsync(context);
        if (failFlush) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else await operation;
        Assert.Equal(expectedStatus, logs.LastContext!.StatusCode);
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(16384, true)]
    public async Task StreamAsync_ConvertedOutputAdapterHonorsConfiguredEventBudget(int budget, bool succeeds)
    {
        var logs = new StubProxyLogService();
        var writer = new CapturingProxyStreamWriter();
        var payload = new Dictionary<string, object?>();
        var channel = new Dictionary<string, object?> { ["id"] = "test", ["type"] = "chat" };
        var context = new ProxyStreamContext(
            Stopwatch.GetTimestamp(), Guid.NewGuid(), "output-budget", "admin", null, payload, payload,
            new Dictionary<string, object?>(), "responses", new ProxyRouteDto(channel, "public", "model", false, true),
            "chat", "test", "superadmin", "model", "public", 120,
            new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()), writer, default);
        var data = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "chat-1", choices = new[] { new { index = 0, delta = new { content = new string('x', 2000) }, finish_reason = (string?)null } }
        });
        var operation = new ProxyStreamService(new SequencedUpstreamClient(
            [("data: " + data, 0), ("", 0), ("data: [DONE]", 0), ("", 0)]),
            logs, new StubWebSearchToolExecutor(), WebSearchTestStore.Create(),
            outputFrames: new() { MaxEventBytes = budget }).StreamAsync(context);
        if (succeeds)
        {
            await operation;
            Assert.Equal(200, logs.LastContext!.StatusCode);
            Assert.Contains(writer.Lines, line => line.Contains("response.completed", StringComparison.Ordinal));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => operation);
            Assert.Equal(500, logs.LastContext!.StatusCode);
            Assert.Empty(writer.Lines);
        }
    }

    private sealed class TerminalDisconnectStream(CancellationTokenSource cancellation, bool failFlush) : MemoryStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            var text = System.Text.Encoding.UTF8.GetString(ToArray());
            if (text.Contains("response.completed", StringComparison.Ordinal) && text.EndsWith("\n\n", StringComparison.Ordinal))
            {
                cancellation.Cancel();
                if (failFlush) return Task.FromCanceled(cancellation.Token);
            }
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("responses", "rate_limit_exceeded", "requests", 429)]
    [InlineData("chat", "rate_limit_exceeded", "requests", 429)]
    [InlineData("messages", "rate_limit_exceeded", "requests", 429)]
    [InlineData("messages", null, "rate_limit_error", 429)]
    [InlineData("responses", "server_error", "server_error", 502)]
    [InlineData("chat", "server_error", "server_error", 502)]
    [InlineData("messages", "server_error", "server_error", 502)]
    public async Task StreamAsync_UpstreamFailure_PreservesErrorAndClassifiesRateLimit(
        string protocol, string? code, string type, int statusCode)
    {
        const string message = "upstream diagnostic contains rate_limit_exceeded as context";
        var errorJson = System.Text.Json.JsonSerializer.Serialize(new { code, type, message });
        var blocks = protocol switch
        {
            "chat" => new[]
            {
                """data: {"id":"partial","choices":[{"index":0,"delta":{"content":"partial"}}]}""",
                "data: {\"error\":" + errorJson + "}"
            },
            "messages" => new[]
            {
                """data: {"type":"message_start","message":{"id":"partial","role":"assistant","content":[]}}""",
                "data: {\"type\":\"error\",\"error\":" + errorJson + "}"
            },
            _ => new[]
            {
                """data: {"type":"response.created","response":{"id":"partial","status":"in_progress"}}""",
                "data: {\"type\":\"response.failed\",\"response\":{\"id\":\"partial\",\"status\":\"failed\",\"error\":" + errorJson + "}}"
            }
        };
        var lines = blocks.SelectMany(block => new[] { (block, 0), ("", 0) }).ToList();
        var logs = new StubProxyLogService();
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?> { ["id"] = "test", ["type"] = protocol };
        var payload = new Dictionary<string, object?>();
        var context = new ProxyStreamContext(
            Stopwatch.GetTimestamp(), Guid.NewGuid(), "classified-stream", "admin", null, payload, payload,
            new Dictionary<string, object?>(), "responses", new ProxyRouteDto(channel, "public", "model", false, true),
            protocol, "test", "superadmin", "model", "public", 120,
            new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()), writer, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<UpstreamException>(() => new ProxyStreamService(
            new SequencedUpstreamClient(lines), logs, new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create()).StreamAsync(context));

        Assert.Equal(statusCode, failure.StatusCode);
        Assert.Equal(statusCode, logs.LastContext!.StatusCode);
        Assert.Contains(message, failure.Message, StringComparison.Ordinal);
        Assert.Contains(code ?? type, logs.LastContext.Error!, StringComparison.Ordinal);
        var captured = Assert.IsType<Dictionary<string, object?>>(logs.LastContext.UpstreamResponse);
        var capturedError = Assert.IsType<Dictionary<string, object?>>(captured["error"]);
        Assert.Equal(code, capturedError["code"]);
        Assert.Equal(type, capturedError["type"]);
        Assert.Equal(message, capturedError["message"]);
        var failureBody = Assert.IsType<Dictionary<string, object?>>(failure.Body);
        Assert.IsType<Dictionary<string, object?>>(failureBody["error"]);
        Assert.DoesNotContain(writer.Lines, line => line.Contains("response.completed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    [InlineData("responses")]
    public async Task StreamAsync_FailedTerminal_ThrowsAndLogsFailure(string protocol)
    {
        var blocks = protocol switch
        {
            "chat" => new[]
            {
                """data: {"id":"partial","choices":[{"index":0,"delta":{"content":"partial"}}]}""",
                """data: {"error":{"type":"server_error","message":"summary failed"}}"""
            },
            "messages" => new[]
            {
                """data: {"type":"message_start","message":{"id":"partial","role":"assistant","content":[]}}""",
                """data: {"type":"error","error":{"type":"server_error","message":"summary failed"}}"""
            },
            _ => new[]
            {
                """data: {"type":"response.created","response":{"id":"partial","status":"in_progress"}}""",
                """data: {"type":"response.failed","response":{"id":"partial","status":"failed","error":{"type":"server_error","message":"summary failed"}}}"""
            }
        };
        var lines = blocks.SelectMany(block => new[] { (block, 0), ("", 0) }).ToList();
        var logs = new StubProxyLogService();
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?> { ["id"] = "test", ["type"] = protocol };
        var payload = new Dictionary<string, object?>();
        var context = new ProxyStreamContext(
            Stopwatch.GetTimestamp(), Guid.NewGuid(), "failed-stream", "admin", null, payload, payload,
            new Dictionary<string, object?>(), "responses", new ProxyRouteDto(channel, "public", "model", false, true),
            protocol, "test", "superadmin", "model", "public", 120,
            new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()), writer, CancellationToken.None);

        await Assert.ThrowsAsync<UpstreamException>(() => new ProxyStreamService(
            new SequencedUpstreamClient(lines), logs, new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create()).StreamAsync(context));

        Assert.Equal(502, logs.LastContext!.StatusCode);
        Assert.NotNull(logs.LastContext.Error);
        Assert.Contains(writer.Lines, line => line.Contains("response.failed", StringComparison.Ordinal));
        Assert.DoesNotContain(writer.Lines, line => line.Contains("response.completed", StringComparison.Ordinal));
        if (protocol != "responses")
        {
            Assert.Equal("failed", logs.LastContext.ResponsePayload!["status"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuiltinStream_UnfinishedCall_DoesNotSearchOrComplete(bool hasDoneMarker)
    {
        var lines = new List<(string Line, int DelayAfterMs)>
        {
            ("data: {\"id\":\"chat_1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"search_1\",\"type\":\"function\",\"function\":{\"name\":\"web_search\",\"arguments\":\"{\\\"query\\\":\"}}]}}]}", 0),
            ("", 0)
        };
        if (hasDoneMarker)
        {
            lines.Add(("data: [DONE]", 0));
            lines.Add(("", 0));
        }
        var logs = new StubProxyLogService();
        var executor = new StubWebSearchToolExecutor();
        var writer = new CapturingProxyStreamWriter();
        var channel = new Dictionary<string, object?> { ["id"] = "test", ["type"] = "chat" };
        var payload = new Dictionary<string, object?>();
        var context = new ProxyStreamContext(
            Stopwatch.GetTimestamp(), Guid.NewGuid(), "test", "admin", null, payload, payload,
            new Dictionary<string, object?>(), "responses", new ProxyRouteDto(channel, "public", "model", false, true),
            "chat", "test", "superadmin", "model", "public", 120,
            new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()), writer, CancellationToken.None)
        {
            BuiltinTools = new BuiltinToolRequestContext { WebSearchToolName = WebSearchRequestPolicy.ToolName }
        };

        await Assert.ThrowsAsync<UpstreamException>(() =>
            new ProxyStreamService(
                new SequencedUpstreamClient(lines),
                logs,
                executor,
                WebSearchTestStore.Create()).StreamAsync(context));

        Assert.Equal(0, executor.Calls);
        Assert.DoesNotContain(writer.Lines, line => line.Contains("response.completed", StringComparison.Ordinal));
        Assert.DoesNotContain(writer.Lines, line => line.Contains("response.function_call_arguments", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuiltinRequest_CancellationAndTimeoutHaveAccurateLogStatus(bool stream, bool clientCancelled)
    {
        using var cancellation = new CancellationTokenSource();
        if (clientCancelled)
        {
            cancellation.Cancel();
        }
        var upstream = new FailingUpstreamClient(new OperationCanceledException(cancellation.Token));
        var logs = new StubProxyLogService();
        var executor = new StubWebSearchToolExecutor();
        var payload = new Dictionary<string, object?>();
        var channel = new Dictionary<string, object?> { ["id"] = "test", ["type"] = "chat" };
        var route = new ProxyRouteDto(channel, "public", "model", false, true);
        var metadata = new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>());
        var binding = new BuiltinToolRequestContext { WebSearchToolName = WebSearchRequestPolicy.ToolName };
        if (stream)
        {
            var context = new ProxyStreamContext(
                Stopwatch.GetTimestamp(), Guid.NewGuid(), "test", "admin", null, payload, payload,
                new Dictionary<string, object?>(), "responses", route, "chat", "test", "superadmin",
                "model", "public", 120, metadata, new CapturingProxyStreamWriter(), cancellation.Token)
            {
                BuiltinTools = binding
            };
            var operation = new ProxyStreamService(
                upstream,
                logs,
                executor,
                WebSearchTestStore.Create()).StreamAsync(context);
            if (clientCancelled)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            }
            else
            {
                await Assert.ThrowsAsync<UpstreamException>(() => operation);
            }
        }
        else
        {
            var context = new ProxyNonStreamContext(
                Stopwatch.GetTimestamp(), Guid.NewGuid(), "test", "admin", null, payload, payload,
                new Dictionary<string, object?>(), "responses", route, "chat", "test", "superadmin",
                "model", "public", 120, metadata, cancellation.Token)
            {
                BuiltinTools = binding
            };
            var operation = new ProxyNonStreamService(
                upstream,
                logs,
                executor,
                WebSearchTestStore.Create()).SendAsync(context);
            if (clientCancelled)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            }
            else
            {
                Assert.IsType<UpstreamException>((await operation).FailureException);
            }
        }
        Assert.Equal(clientCancelled ? 499 : 504, logs.LastContext!.StatusCode);
        Assert.NotNull(logs.LastContext.Error);
    }

    [Fact]
    public async Task StreamAsync_ConvertedMessages_StreamsReasoningAfterBufferedToolContractValidation()
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
        var service = new ProxyStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create());
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
            payload: new Dictionary<string, object?>
            {
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["type"] = "function", ["name"] = "exec_command" }
                }
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

        Assert.Contains(writer.Lines, line => line.Contains("response.reasoning_summary_text.delta", StringComparison.Ordinal));
        Assert.Contains(writer.Lines, line => line.Contains("response.function_call_arguments.delta", StringComparison.Ordinal));
        Assert.NotNull(logs.LastContext);
        Assert.True(logs.LastContext!.DurationMs >= 50);
        Assert.NotNull(logs.LastContext.TtftMs);
    }

    [Fact]
    public async Task StreamAsync_DeclaredWebSearchWithoutCall_UsesNormalUpstream()
    {
        var upstream = new SequencedUpstreamClient(
        [
            ("event: message_start", 0),
            ("data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"upstream-model\",\"usage\":{\"input_tokens\":1}}}", 0),
            ("", 0),
            ("event: content_block_start", 0),
            ("data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}", 0),
            ("", 0),
            ("event: content_block_delta", 0),
            ("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ordinary answer\"}}", 0),
            ("", 0),
            ("event: content_block_stop", 0),
            ("data: {\"type\":\"content_block_stop\",\"index\":0}", 0),
            ("", 0),
            ("event: message_delta", 0),
            ("data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}", 0),
            ("", 0),
            ("event: message_stop", 0),
            ("data: {\"type\":\"message_stop\"}", 0),
            ("", 0)
        ]);
        var logs = new StubProxyLogService();
        var webSearch = new StubWebSearchToolExecutor();
        var payload = new Dictionary<string, object?>
        {
            ["input"] = "Hello",
            ["tools"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "web_search" } }
        };
        var binding = WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "messages", "superadmin");
        var service = new ProxyStreamService(upstream, logs, webSearch, WebSearchTestStore.Create());
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
            payload: payload,
            upstreamRequest: ProtocolConverter.ConvertRequest(payload, "responses", "messages", "upstream-model"),
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
            cancellationToken: CancellationToken.None)
        {
            BuiltinTools = binding
        };

        await service.StreamAsync(context);

        Assert.Equal(0, webSearch.Calls);
        Assert.Equal(1, upstream.Calls);
        Assert.True(writer.Prepared);
        Assert.Contains("ordinary answer", string.Concat(writer.Lines), StringComparison.Ordinal);
        Assert.Single(writer.Lines, line => line.StartsWith("event: response.created\n", StringComparison.Ordinal));
        Assert.Single(writer.Lines, line => line.StartsWith("event: response.completed\n", StringComparison.Ordinal));
        Assert.Equal("data: [DONE]\n\n", writer.Lines[^1]);
        Assert.Null(logs.LastContext?.WebSearchDetails);
        Assert.Null(logs.LastContext?.Error);
    }

    [Fact]
    public async Task StreamAsync_UndeclaredWebSearchName_AfterSemanticOutputDoesNotReplay()
    {
        var upstream = new RetrySequenceUpstreamClient(
        [
            [
                """data: {"id":"chat-invalid","choices":[{"index":0,"delta":{"reasoning_content":"discarded reasoning"}}]}""",
                "",
                """data: {"id":"chat-invalid","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"search_invalid","type":"function","function":{"name":"opensearch_web_search","arguments":"{\"query\":\"xlog\"}"}}]}}]}""",
                "",
                """data: {"id":"chat-invalid","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":2}}""",
                "",
                "data: [DONE]"
            ],
            [
                """data: {"id":"chat-valid","choices":[{"index":0,"delta":{"content":"recovered answer"}}]}""",
                "",
                """data: {"id":"chat-valid","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":3}}""",
                "",
                "data: [DONE]"
            ]
        ]);
        var logs = new StubProxyLogService();
        var payload = new Dictionary<string, object?>
        {
            ["input"] = "Search for the xlog decoder.",
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "web_search" },
                new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["name"] = "lookup",
                    ["parameters"] = new Dictionary<string, object?>()
                }
            }
        };
        var binding = WebSearchRequestPolicy.RegisterBuiltin(
            payload, WebSearchModes.Simulate, ProtocolConverter.Responses, ProtocolConverter.Chat, "superadmin");
        var upstreamRequest = ProtocolConverter.ConvertRequest(
            payload, ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream-model");
        WebSearchRequestPolicy.FinalizeUpstreamRequest(upstreamRequest, binding);
        var writer = new CapturingProxyStreamWriter();
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req_undeclared_web_search",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: payload,
            payload: payload,
            upstreamRequest: upstreamRequest,
            entryProtocol: ProtocolConverter.Responses,
            route: new ProxyRouteDto(
                new Dictionary<string, object?> { ["id"] = "chat", ["type"] = ProtocolConverter.Chat },
                "public-model",
                "upstream-model",
                supportsImage: false,
                matchedModelMapping: true),
            channelType: ProtocolConverter.Chat,
            channelId: "chat",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None)
        {
            BuiltinTools = binding
        };

        await Assert.ThrowsAsync<UpstreamException>(()=>new ProxyStreamService(upstream,logs,new StubWebSearchToolExecutor(),WebSearchTestStore.Create()).StreamAsync(context));
        Assert.Equal(1,upstream.Calls);
        var output=string.Concat(writer.Lines);
        Assert.Contains("discarded reasoning",output,StringComparison.Ordinal);
        Assert.DoesNotContain("recovered answer",output,StringComparison.Ordinal);
        Assert.DoesNotContain("response.completed",output,StringComparison.Ordinal);
        Assert.Single(writer.Lines,line=>line.StartsWith("event: error\n",StringComparison.Ordinal));
        Assert.Equal(502,logs.LastContext!.StatusCode);
    }

    [Fact]
    public async Task StreamAsync_UndeclaredWebSearchName_DoesNotRetryAfterCreated()
    {
        var upstream = new RetrySequenceUpstreamClient(
        [
            InvalidWebSearchStream("first-invalid", "first discarded reasoning", 10, 2),
            InvalidWebSearchStream("second-invalid", "second discarded reasoning", 20, 3)
        ]);
        var logs = new StubProxyLogService();
        var (payload, upstreamRequest, binding) = WebSearchRetryRequest();
        var writer = new CapturingProxyStreamWriter();
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req_undeclared_web_search_exhausted",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: payload,
            payload: payload,
            upstreamRequest: upstreamRequest,
            entryProtocol: ProtocolConverter.Responses,
            route: new ProxyRouteDto(
                new Dictionary<string, object?> { ["id"] = "chat", ["type"] = ProtocolConverter.Chat },
                "public-model",
                "upstream-model",
                supportsImage: false,
                matchedModelMapping: true),
            channelType: ProtocolConverter.Chat,
            channelId: "chat",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None)
        {
            BuiltinTools = binding
        };

        var error = await Assert.ThrowsAsync<UpstreamException>(() => new ProxyStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create()).StreamAsync(context));

        Assert.True(ToolContractRetryPolicy.IsUndeclaredToolCall(error));
        Assert.Equal(1,upstream.Calls);
        Assert.Contains("first discarded reasoning",string.Concat(writer.Lines));
        Assert.DoesNotContain("response.completed",string.Concat(writer.Lines));
        Assert.Single(writer.Lines,line=>line.StartsWith("event: error\n",StringComparison.Ordinal));
        Assert.Equal(10L,logs.LastContext!.AggregatedUsage!["prompt_tokens"]);
        Assert.Equal(2L,logs.LastContext.AggregatedUsage["completion_tokens"]);
    }

    [Fact]
    public async Task StreamAsync_LateUndeclaredWebSearchName_EndsWithFailedTerminal()
    {
        var upstream = new RetrySequenceUpstreamClient(
        [
            new[]
            {
                SseData(new
                {
                    id = "chat-search",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new
                            {
                                tool_calls = new[]
                                {
                                    new
                                    {
                                        index = 0,
                                        id = "search-valid",
                                        type = "function",
                                        function = new
                                        {
                                            name = WebSearchRequestPolicy.ToolName,
                                            arguments = "{\"query\":\"xlog\"}"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }),
                "",
                SseData(new
                {
                    id = "chat-search",
                    choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } }
                }),
                "",
                "data: [DONE]"
            },
            InvalidWebSearchStream("late-invalid-1", "late discarded reasoning 1", 10, 2),
            InvalidWebSearchStream("late-invalid-2", "late discarded reasoning 2", 20, 3)
        ]);
        var logs = new StubProxyLogService();
        var executor = new SuccessfulWebSearchToolExecutor();
        var (payload, upstreamRequest, binding) = WebSearchRetryRequest();
        var writer = new CapturingProxyStreamWriter();
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req_late_undeclared_web_search",
            ownerUsername: "admin",
            apiKeyId: Guid.NewGuid(),
            originalPayload: payload,
            payload: payload,
            upstreamRequest: upstreamRequest,
            entryProtocol: ProtocolConverter.Responses,
            route: new ProxyRouteDto(
                new Dictionary<string, object?> { ["id"] = "chat", ["type"] = ProtocolConverter.Chat },
                "public-model",
                "upstream-model",
                supportsImage: false,
                matchedModelMapping: true),
            channelType: ProtocolConverter.Chat,
            channelId: "chat",
            ownerRole: "superadmin",
            upstreamModel: "upstream-model",
            requestModel: "public-model",
            defaultTimeout: 120,
            requestMetadata: new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            streamWriter: writer,
            cancellationToken: CancellationToken.None)
        {
            BuiltinTools = binding
        };

        await Assert.ThrowsAsync<UpstreamException>(() => new ProxyStreamService(
            upstream,
            logs,
            executor,
            WebSearchTestStore.Create()).StreamAsync(context));

        Assert.Equal(2, upstream.Calls);
        Assert.Equal(1, executor.Calls);
        var output = string.Concat(writer.Lines);
        Assert.Contains("event: error", output, StringComparison.Ordinal);
        Assert.Contains("late discarded reasoning", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\":\"opensearch_web_search\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("response.completed", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_UndeclaredWebSearchName_RetriesAndAggregatesUsage()
    {
        var upstream = new RetrySequenceJsonUpstreamClient(
        [
            InvalidWebSearchResponse("first-invalid", "first discarded reasoning", 10, 2),
            new Dictionary<string, object?>
            {
                ["id"] = "chat-valid",
                ["object"] = "chat.completion",
                ["choices"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = "recovered answer"
                        },
                        ["finish_reason"] = "stop"
                    }
                },
                ["usage"] = new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 20,
                    ["completion_tokens"] = 3
                }
            }
        ]);
        var logs = new StubProxyLogService();
        var (payload, upstreamRequest, binding) = WebSearchRetryRequest();
        var context = new ProxyNonStreamContext(
            Stopwatch.GetTimestamp(),
            Guid.NewGuid(),
            "req_undeclared_web_search_nonstream",
            "admin",
            Guid.NewGuid(),
            payload,
            payload,
            upstreamRequest,
            ProtocolConverter.Responses,
            new ProxyRouteDto(
                new Dictionary<string, object?> { ["id"] = "chat", ["type"] = ProtocolConverter.Chat },
                "public-model",
                "upstream-model",
                supportsImage: false,
                matchedModelMapping: true),
            ProtocolConverter.Chat,
            "chat",
            "superadmin",
            "upstream-model",
            "public-model",
            120,
            new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            CancellationToken.None)
        {
            BuiltinTools = binding
        };

        var result = await new ProxyNonStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create()).SendAsync(context);

        Assert.Null(result.FailureException);
        Assert.Equal(2, upstream.Calls);
        Assert.Contains("recovered answer", WebSearchPayload.JsonDumps(result.Payload), StringComparison.Ordinal);
        Assert.DoesNotContain("first discarded reasoning", WebSearchPayload.JsonDumps(result.Payload), StringComparison.Ordinal);
        Assert.NotNull(logs.LastContext);
        Assert.Equal(30L, logs.LastContext!.AggregatedUsage!["prompt_tokens"]);
        Assert.Equal(5L, logs.LastContext.AggregatedUsage["completion_tokens"]);
        Assert.Single(WebSearchPayload.ListValue(logs.LastContext.WebSearchDetails!, "retries"));
        var retryMessages = WebSearchPayload.ListValue(upstream.Payloads[1], "messages")
            .OfType<Dictionary<string, object?>>()
            .ToList();
        Assert.Contains(retryMessages, message =>
            message["role"]?.ToString() == "system"
            && message["content"]?.ToString()?.Contains("web_search", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task StreamAsync_UndeclaredCodeModeName_AfterSemanticOutputDoesNotReplay()
    {
        var upstream = new RetrySequenceUpstreamClient(
        [
            [
                SseData(new
                {
                    id = "chat-invalid",
                    choices = new[] { new { index = 0, delta = new { reasoning_content = "discarded reasoning" } } }
                }),
                "",
                SseData(new
                {
                    id = "chat-invalid",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new
                            {
                                tool_calls = new[]
                                {
                                    new
                                    {
                                        index = 0,
                                        id = "invalid-call",
                                        type = "function",
                                        function = new
                                        {
                                            name = "exec_command",
                                            arguments = "{\"input\":\"const value = 1;\"}"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }),
                "",
                SseData(new
                {
                    id = "chat-invalid",
                    choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 2 }
                }),
                "",
                "data: [DONE]"
            ],
            [
                SseData(new
                {
                    id = "chat-valid",
                    choices = new[] { new { index = 0, delta = new { content = "recovered answer" } } }
                }),
                "",
                SseData(new
                {
                    id = "chat-valid",
                    choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 20, completion_tokens = 3 }
                }),
                "",
                "data: [DONE]"
            ]
        ]);
        var logs = new StubProxyLogService();
        var (payload, upstreamRequest) = CodeModeRetryRequest();
        var writer = new CapturingProxyStreamWriter();
        var context = new ProxyStreamContext(
            Stopwatch.GetTimestamp(),
            Guid.NewGuid(),
            "req_undeclared_code_mode",
            "admin",
            Guid.NewGuid(),
            payload,
            payload,
            upstreamRequest,
            ProtocolConverter.Responses,
            new ProxyRouteDto(
                new Dictionary<string, object?> { ["id"] = "chat", ["type"] = ProtocolConverter.Chat },
                "public-model",
                "upstream-model",
                supportsImage: false,
                matchedModelMapping: true),
            ProtocolConverter.Chat,
            "chat",
            "superadmin",
            "upstream-model",
            "public-model",
            120,
            new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            writer,
            CancellationToken.None);

        await Assert.ThrowsAsync<UpstreamException>(()=>new ProxyStreamService(upstream,logs,new StubWebSearchToolExecutor(),WebSearchTestStore.Create()).StreamAsync(context));
        Assert.Equal(1,upstream.Calls);
        var output=string.Concat(writer.Lines);
        Assert.Contains("discarded reasoning",output,StringComparison.Ordinal);
        Assert.DoesNotContain("recovered answer",output,StringComparison.Ordinal);
        Assert.DoesNotContain("response.completed",output,StringComparison.Ordinal);
        Assert.Single(writer.Lines,line=>line.StartsWith("event: error\n",StringComparison.Ordinal));
        Assert.Equal(502,logs.LastContext!.StatusCode);
    }

    [Fact]
    public async Task SendAsync_UndeclaredCodeModeName_RetriesWithBareExecContract()
    {
        var upstream = new RetrySequenceJsonUpstreamClient(
        [
            new Dictionary<string, object?>
            {
                ["id"] = "chat-invalid",
                ["choices"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = "",
                            ["tool_calls"] = new List<object?>
                            {
                                new Dictionary<string, object?>
                                {
                                    ["id"] = "invalid-call",
                                    ["type"] = "function",
                                    ["function"] = new Dictionary<string, object?>
                                    {
                                        ["name"] = "exec_command",
                                        ["arguments"] = "{\"input\":\"const value = 1;\"}"
                                    }
                                }
                            }
                        },
                        ["finish_reason"] = "tool_calls"
                    }
                },
                ["usage"] = new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 10,
                    ["completion_tokens"] = 2
                }
            },
            new Dictionary<string, object?>
            {
                ["id"] = "chat-valid",
                ["object"] = "chat.completion",
                ["choices"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = "recovered answer"
                        },
                        ["finish_reason"] = "stop"
                    }
                },
                ["usage"] = new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 20,
                    ["completion_tokens"] = 3
                }
            }
        ]);
        var logs = new StubProxyLogService();
        var (payload, upstreamRequest) = CodeModeRetryRequest();
        var context = new ProxyNonStreamContext(
            Stopwatch.GetTimestamp(),
            Guid.NewGuid(),
            "req_undeclared_code_mode_nonstream",
            "admin",
            Guid.NewGuid(),
            payload,
            payload,
            upstreamRequest,
            ProtocolConverter.Responses,
            new ProxyRouteDto(
                new Dictionary<string, object?> { ["id"] = "chat", ["type"] = ProtocolConverter.Chat },
                "public-model",
                "upstream-model",
                supportsImage: false,
                matchedModelMapping: true),
            ProtocolConverter.Chat,
            "chat",
            "superadmin",
            "upstream-model",
            "public-model",
            120,
            new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            CancellationToken.None);

        var result = await new ProxyNonStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create()).SendAsync(context);

        Assert.Null(result.FailureException);
        Assert.Equal(2, upstream.Calls);
        Assert.Contains("recovered answer", WebSearchPayload.JsonDumps(result.Payload));
        Assert.DoesNotContain("first discarded reasoning", WebSearchPayload.JsonDumps(result.Payload));
        var retryMessages = WebSearchPayload.ListValue(upstream.Payloads[1], "messages")
            .OfType<Dictionary<string, object?>>()
            .ToList();
        Assert.Contains(retryMessages, message =>
            message["role"]?.ToString() == "system"
            && message["content"]?.ToString()?.Contains("Allowed tool names: `exec`", StringComparison.Ordinal) == true);
        Assert.Equal(30L, logs.LastContext!.AggregatedUsage!["prompt_tokens"]);
        Assert.Equal(5L, logs.LastContext.AggregatedUsage["completion_tokens"]);
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
        var service = new ProxyStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create());
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
        Assert.Equal("upstream returned HTTP 429｜终止:UpstreamError", logs.LastContext.Error);
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
        var service = new ProxyStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create());
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
        var service = new ProxyStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create());
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
    public async Task StreamAsync_BuiltinToolUpstream429BeforeFirstLine_DoesNotPrepareSseOrSearch()
    {
        var upstream = new FailingUpstreamClient(new UpstreamException(
            "upstream returned HTTP 429",
            ProxyHttpStatus.TooManyRequests));
        var logs = new StubProxyLogService();
        var executor = new StubWebSearchToolExecutor();
        var service = new ProxyStreamService(upstream, logs, executor, WebSearchTestStore.Create());
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
            requestId: "req_builtin_no_prepare",
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
            cancellationToken: CancellationToken.None)
        {
            BuiltinTools = new BuiltinToolRequestContext
            {
                WebSearchToolName = WebSearchRequestPolicy.ToolName
            }
        };

        var exception = await Assert.ThrowsAsync<UpstreamException>(() => service.StreamAsync(context));

        Assert.Equal(429, exception.StatusCode);
        Assert.Equal(0, executor.Calls);
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
            ("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}", 0),
            ("", 0)
        ]);
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create());
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
        // Service now hands the writer complete events (created/completed/DONE), not physical lines.
        Assert.Equal(3, writer.Lines.Count);
    }

    [Fact]
    public async Task StreamAsync_PassThrough_DoesNotPersistStreamLines()
    {
        var upstream = new SequencedUpstreamClient(
        [
            ("event: response.output_text.delta", 0),
            ("data: {\"delta\":\"hello\"}", 0),
            ("", 0),
            ("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}", 0),
            ("", 0)
        ]);
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create());
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
        Assert.Null(logs.LastContext!.Error);
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
        var service = new ProxyStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create());
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

        Assert.Equal(string.Join("\n", upstreamLines[..3]) + "\ndata: [DONE]\n\n", string.Concat(writer.Lines));
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
        var service = new ProxyStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create());
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
        Assert.Null(logs.LastContext?.Error);
    }

    [Fact]
    public async Task StreamAsync_ConvertedChat_CompletesWithoutStreamLineLogging()
    {
        var upstream = new SequencedUpstreamClient(
        [
            ("data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}", 0),
            ("", 0),
            ("data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1,\"total_tokens\":6}}", 0),
            ("", 0),
            ("data: [DONE]", 0)
        ]);
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create());
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
        Assert.Null(logs.LastContext?.Error);
    }

    [Fact]
    public async Task CaptureStreamLogLines_KeepsOnlyLastDataLine()
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
            "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"gpt-5\",\"status\":\"completed\"}}",
            "",
            "data: [DONE]",
            ""
        };
        var capture = new StreamLogCapture(ProtocolConverter.Responses);
        var forwarded = new List<string>();

        await foreach (var line in ProxyStreamService.CaptureStreamLogLines(
            ToAsyncEnumerable(input),
            capture,
            CancellationToken.None))
        {
            forwarded.Add(line);
        }

        Assert.Equal(input, forwarded);
        Assert.True(capture.SawTerminalEvent);
        Assert.Equal(
            "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"gpt-5\",\"status\":\"completed\"}}",
            capture.LastDataLine);
    }

    [Fact]
    public async Task StreamAsync_PassThroughTruncated_LogsUnexpectedEndWithLastDataLine()
    {
        var upstream = new SequencedUpstreamClient(
        [
            ("event: response.output_text.delta", 0),
            ("data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}", 0),
            ("", 0)
        ]);
        var logs = new StubProxyLogService();
        var service = new ProxyStreamService(
            upstream,
            logs,
            new StubWebSearchToolExecutor(),
            WebSearchTestStore.Create());
        var context = new ProxyStreamContext(
            startedTimestamp: Stopwatch.GetTimestamp(),
            requestLogId: Guid.NewGuid(),
            requestId: "req-truncated",
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
            streamWriter: new CapturingProxyStreamWriter(),
            cancellationToken: CancellationToken.None);

        await Assert.ThrowsAsync<UpstreamException>(() => service.StreamAsync(context));

        Assert.NotNull(logs.LastContext);
        Assert.Equal(502, logs.LastContext!.StatusCode);
        Assert.Equal(
            "upstream stream ended before its terminal event｜终止:UpstreamError｜最后SSE:data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}",
            logs.LastContext.Error);
    }

    [Fact]
    public void StreamLogCapture_ComposeErrorText_TruncatesAtLimit()
    {
        var capture = new StreamLogCapture(ProtocolConverter.Responses);
        capture.Observe($"data: {new string('x', 3000)}");

        var text = capture.ComposeErrorText(null, StreamCaptureTermination.UnexpectedEnd);

        Assert.Equal(StreamLogCapture.MaxErrorTextLength, text.Length);
        Assert.StartsWith("upstream stream ended before its terminal event｜终止:UnexpectedEnd｜最后SSE:data: ", text, StringComparison.Ordinal);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
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

    private static (
        Dictionary<string, object?> Payload,
        Dictionary<string, object?> UpstreamRequest,
        BuiltinToolRequestContext Binding) WebSearchRetryRequest()
    {
        var payload = new Dictionary<string, object?>
        {
            ["input"] = "Search for the xlog decoder.",
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "web_search" },
                new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["name"] = "lookup",
                    ["parameters"] = new Dictionary<string, object?>()
                }
            }
        };
        var binding = WebSearchRequestPolicy.RegisterBuiltin(
            payload, WebSearchModes.Simulate, ProtocolConverter.Responses, ProtocolConverter.Chat, "superadmin")!;
        var upstreamRequest = ProtocolConverter.ConvertRequest(
            payload, ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream-model");
        WebSearchRequestPolicy.FinalizeUpstreamRequest(upstreamRequest, binding);
        return (payload, upstreamRequest, binding);
    }

    private static (Dictionary<string, object?> Payload, Dictionary<string, object?> UpstreamRequest) CodeModeRetryRequest()
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = "public-model",
            ["input"] = "Run the JavaScript.",
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "namespace",
                    ["name"] = "functions",
                    ["tools"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "custom",
                            ["name"] = "exec",
                            ["description"] = "Run JavaScript. Nested methods include exec_command.",
                            ["format"] = new Dictionary<string, object?> { ["type"] = "text" }
                        }
                    }
                }
            }
        };
        return (
            payload,
            ProtocolConverter.ConvertRequest(
                payload, ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream-model"));
    }

    private static string[] InvalidWebSearchStream(
        string id,
        string reasoning,
        int promptTokens,
        int completionTokens)
    {
        return
        [
            SseData(new
            {
                id,
                choices = new[] { new { index = 0, delta = new { reasoning_content = reasoning } } }
            }),
            "",
            SseData(new
            {
                id,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = 0,
                                    id = $"{id}-tool",
                                    type = "function",
                                    function = new
                                    {
                                        name = "opensearch_web_search",
                                        arguments = "{\"query\":\"xlog\"}"
                                    }
                                }
                            }
                        }
                    }
                }
            }),
            "",
            SseData(new
            {
                id,
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } },
                usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens }
            }),
            "",
            "data: [DONE]"
        ];
    }

    private static string SseData(object payload) =>
        $"data: {System.Text.Json.JsonSerializer.Serialize(payload)}";

    private static Dictionary<string, object?> InvalidWebSearchResponse(
        string id,
        string reasoning,
        int promptTokens,
        int completionTokens)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["object"] = "chat.completion",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = "",
                        ["reasoning_content"] = reasoning,
                        ["tool_calls"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = $"{id}-tool",
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = "opensearch_web_search",
                                    ["arguments"] = "{\"query\":\"xlog\"}"
                                }
                            }
                        }
                    },
                    ["finish_reason"] = "tool_calls"
                }
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = promptTokens,
                ["completion_tokens"] = completionTokens
            }
        };
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

    [Theory]
    [InlineData("chat","chat")][InlineData("messages","messages")][InlineData("responses","responses")]
    [InlineData("responses","chat")][InlineData("responses","messages")][InlineData("chat","messages")]
    [InlineData("chat","responses")][InlineData("messages","chat")][InlineData("messages","responses")]
    public async Task StreamAsync_FullAuditIsNotTruncatedAndSharedCaptureDoesNotDuplicateDeltas(string entry,string upstreamProtocol)
    {
        var directory=Path.Combine(Path.GetTempPath(),"ocx-proxy-full-audit-"+Guid.NewGuid().ToString("N"));
        var spool=new OpenCodex.CoreBase.Domain.Proxy.ProxyAuditSpool(new() { Directory=directory,MemoryThresholdBytes=32768 });
        try
        {
            var fragment=new string('x',4096);var expected=string.Concat(Enumerable.Repeat(fragment,300));
            var frames=new List<string>();
            void Add(string type,Dictionary<string,object?> payload)
            { payload["type"]=type;frames.Add(SseFrame.FromJson(payload,type).WireText); }
            if(upstreamProtocol=="messages")
            {
                Add("message_start",new() { ["message"]=new Dictionary<string,object?> { ["id"]="m",["model"]="upstream-model",["usage"]=new Dictionary<string,object?> { ["input_tokens"]=7 } } });
                Add("content_block_start",new() { ["index"]=0,["content_block"]=new Dictionary<string,object?> { ["type"]="text",["text"]="" } });
            }
            if(upstreamProtocol=="responses")Add("response.created",new() { ["response"]=new Dictionary<string,object?> { ["id"]="r",["model"]="upstream-model",["status"]="in_progress" } });
            for(var n=0;n<300;n++)
            {
                if(upstreamProtocol=="chat")frames.Add(SseFrame.FromJson(new Dictionary<string,object?> { ["id"]="c",["model"]="upstream-model",["choices"]=new object?[] { new Dictionary<string,object?> { ["index"]=0,["delta"]=new Dictionary<string,object?> { ["role"]="assistant",["content"]=fragment } } } }).WireText);
                else if(upstreamProtocol=="messages")Add("content_block_delta",new() { ["index"]=0,["delta"]=new Dictionary<string,object?> { ["type"]="text_delta",["text"]=fragment } });
                else Add("response.output_text.delta",new() { ["output_index"]=0,["content_index"]=0,["delta"]=fragment });
            }
            if(upstreamProtocol=="chat")
            {
                frames.Add(SseFrame.FromJson(new Dictionary<string,object?> { ["choices"]=new object?[] { new Dictionary<string,object?> { ["index"]=0,["delta"]=new Dictionary<string,object?>(),["finish_reason"]="stop" } },["usage"]=new Dictionary<string,object?> { ["prompt_tokens"]=7,["completion_tokens"]=11 } }).WireText);frames.Add(SseFrame.Done.WireText);
            }
            else if(upstreamProtocol=="messages")
            {
                Add("content_block_stop",new() { ["index"]=0 });
                Add("message_delta",new() { ["delta"]=new Dictionary<string,object?> { ["stop_reason"]="end_turn" },["usage"]=new Dictionary<string,object?> { ["output_tokens"]=11 } });Add("message_stop",new());
            }
            else Add("response.completed",new() { ["response"]=new Dictionary<string,object?> { ["id"]="r",["status"]="completed",["output"]=new List<object?>(),["usage"]=new Dictionary<string,object?> { ["input_tokens"]=7,["output_tokens"]=11 } } });
            var logs=new StubProxyLogService();
            var service=new ProxyStreamService(new SequencedUpstreamClient(frames.Select(frame=>(frame,0)).ToArray()),logs,new StubWebSearchToolExecutor(),WebSearchTestStore.Create(),outputFrames:new SseFrameOptions { MaxEventBytes=4*1024*1024 },auditSpool:spool);
            var context=new ProxyStreamContext(startedTimestamp:Stopwatch.GetTimestamp(),requestLogId:Guid.NewGuid(),requestId:"large-audit",ownerUsername:"test",apiKeyId:Guid.NewGuid(),
                originalPayload:new(),payload:new(),upstreamRequest:new(),entryProtocol:entry,
                route:new ProxyRouteDto(new Dictionary<string,object?> { ["id"]="test",["type"]=upstreamProtocol },"public-model","upstream-model",false,true),
                channelType:upstreamProtocol,channelId:"test",ownerRole:"superadmin",upstreamModel:"upstream-model",requestModel:"public-model",defaultTimeout:120,
                requestMetadata:new ProxyRequestMetadata("POST","/test",null,new Dictionary<string,string>()),streamWriter:new CapturingProxyStreamWriter(),cancellationToken:default);
            await service.StreamAsync(context);
            Assert.Null(logs.LastContext!.Error);
            var body=System.Text.Json.JsonSerializer.Serialize(logs.LastContext.UpstreamResponse);
            Assert.Contains(expected,body);Assert.DoesNotContain(expected+fragment,body);Assert.DoesNotContain("_opencodex_capture",body);
            var usage=Assert.IsType<Dictionary<string,object?>>(logs.LastContext.UpstreamResponse!["usage"]);
            Assert.Equal(7,Convert.ToInt32(usage[upstreamProtocol=="chat"?"prompt_tokens":"input_tokens"]));
            Assert.Equal(11,Convert.ToInt32(usage[upstreamProtocol=="chat"?"completion_tokens":"output_tokens"]));
            Assert.Equal((0L,0),spool.Usage);Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally { Directory.Delete(directory,true); }
    }

    [Theory]
    [InlineData("chat","chat")][InlineData("messages","messages")][InlineData("responses","responses")]
    [InlineData("responses","chat")][InlineData("responses","messages")][InlineData("chat","messages")]
    [InlineData("chat","responses")][InlineData("messages","chat")][InlineData("messages","responses")]
    public async Task Incremental_FirstSemanticEventArrivesBeforeUpstreamTerminal_AndTerminalAfterAudit(string entry,string protocol)
    {
        var first=protocol switch
        {
            "chat"=>"data: {\"id\":\"c\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n",
            "messages"=>"event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"role\":\"assistant\",\"content\":[]}}\n\n",
            _=>"event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"status\":\"in_progress\"}}\n\n"
        };
        var terminal=protocol switch { "chat"=>"data: [DONE]\n\n","messages"=>"event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",_=>"event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"output\":[]}}\n\n" };
        var source=new BarrierClient(first,terminal);var logs=new StubProxyLogService();var writer=new IncrementalWriter { Audit=()=>Assert.NotNull(logs.LastContext) };
        var running=new ProxyStreamService(source,logs,new StubWebSearchToolExecutor(),WebSearchTestStore.Create()).StreamAsync(IncrementalContext(entry,protocol,writer));
        try { await writer.First.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(running.IsCompleted);Assert.Null(logs.LastContext); }
        finally { source.AllowTerminal.TrySetResult(); }
        await running;Assert.Contains(writer.Frames,f=>f.IsTerminal);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Incremental_AuditFailureNeverPublishesSuccess_AndTerminalFlushFailureUpdatesFailureLog(bool failFlush)
    {
        var logs=new StubProxyLogService();
        if(!failFlush)logs.CompleteHook=_=>throw new IOException("audit disk full");
        var writer=new IncrementalWriter { ThrowTerminal=failFlush };
        var source=new BarrierClient("data: {\"id\":\"c\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"}}]}\n\n","data: [DONE]\n\n");source.AllowTerminal.TrySetResult();
        await Assert.ThrowsAsync<IOException>(()=>new ProxyStreamService(source,logs,new StubWebSearchToolExecutor(),WebSearchTestStore.Create()).StreamAsync(IncrementalContext("chat","chat",writer)));
        Assert.DoesNotContain(writer.Frames,f=>f.IsDone);
        if(failFlush) { Assert.Equal(500,logs.LastContext!.StatusCode);Assert.Contains("Terminal",logs.LastContext.Error); }
    }
    [Fact]
    public async Task Incremental_CancelAfterSpilledDeltaReleasesAllBuffersAndDoesNotPublishTerminal()
    {
        var directory=Path.Combine(Path.GetTempPath(),"ocx-cancel-live-"+Guid.NewGuid().ToString("N"));
        var spool=new ProxyAuditSpool(new() { Directory=directory,MemoryThresholdBytes=0 });
        using var ct=new CancellationTokenSource();var writer=new IncrementalWriter();
        var first=SseFrame.FromJson(new Dictionary<string,object?> { ["id"]="c",["choices"]=new object?[] { new Dictionary<string,object?> { ["index"]=0,["delta"]=new Dictionary<string,object?> { ["content"]=new string('x',4096) } } } }).WireText;
        var source=new BarrierClient(first,"data: [DONE]\n\n");
        try
        {
            var running=new ProxyStreamService(source,new StubProxyLogService(),new StubWebSearchToolExecutor(),WebSearchTestStore.Create(),auditSpool:spool)
                .StreamAsync(IncrementalContext("responses","chat",writer,ct.Token));
            await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(spool.Usage.Bytes>0);
            ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>running);
            Assert.DoesNotContain(writer.Frames,f=>f.IsTerminal);Assert.Equal((0L,0),spool.Usage);Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally { ct.Cancel();source.AllowTerminal.TrySetResult();Directory.Delete(directory,true); }
    }

    private static ProxyStreamContext IncrementalContext(string entry,string protocol,IProxyStreamWriter writer,CancellationToken ct=default)
    {
        var payload=new Dictionary<string,object?> { ["tools"]=new List<object?> { new Dictionary<string,object?> { ["type"]="function",["name"]="declared",["parameters"]=new Dictionary<string,object?> { ["type"]="object" } } } };
        return new ProxyStreamContext(Stopwatch.GetTimestamp(),Guid.NewGuid(),"incremental","test",Guid.NewGuid(),payload,payload,new(),entry,
            new ProxyRouteDto(new Dictionary<string,object?> { ["id"]="test",["type"]=protocol },"public","model",false,true),protocol,"test","superadmin","model","public",120,
            new ProxyRequestMetadata("POST","/test",null,new Dictionary<string,string>()),writer,ct);
    }
    private sealed class IncrementalWriter:IProxyStreamWriter
    {
        public readonly TaskCompletionSource First=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<SseFrame> Frames=[];
        public Action? Audit;public bool ThrowTerminal;
        public void PrepareSse() { }
        public Task<StreamWriteMetrics> WriteLinesAsync(IAsyncEnumerable<string> lines,Func<string,bool> count,Func<int> elapsed,CancellationToken ct=default)=>throw new NotSupportedException();
        public async Task<StreamWriteMetrics> WriteFramesAsync(IAsyncEnumerable<SseFrame> frames,Func<string,bool> count,Func<int> elapsed,CancellationToken ct=default)
        {
            await foreach(var frame in frames.WithCancellation(ct))
            {
                if(frame.IsTerminal) { Audit?.Invoke();if(ThrowTerminal)throw new IOException("terminal flush failed"); }
                Frames.Add(frame);if(frame.IsSemantic)First.TrySetResult();
            }
            return new();
        }
    }
    private sealed class BarrierClient(string first,string terminal):IUpstreamClient
    {
        public readonly TaskCompletionSource AllowTerminal=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Waiting=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Dictionary<string,object?>> PostJsonAsync(IReadOnlyDictionary<string,object?> channel,IReadOnlyDictionary<string,object?> payload,int timeout,CancellationToken ct)=>throw new NotSupportedException();
        public async IAsyncEnumerable<string> StreamJsonAsync(IReadOnlyDictionary<string,object?> channel,IReadOnlyDictionary<string,object?> payload,int timeout,[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { yield return first;Waiting.TrySetResult();await AllowTerminal.Task.WaitAsync(ct);yield return terminal; }
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
            throw _exception;
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
        public int Calls { get; private set; }

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
            Calls++;
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

    private sealed class RetrySequenceUpstreamClient : IUpstreamClient
    {
        private readonly Queue<string[]> _responses;

        public RetrySequenceUpstreamClient(IEnumerable<string[]> responses)
        {
            _responses = new Queue<string[]>(responses);
        }

        public int Calls { get; private set; }

        public List<Dictionary<string, object?>> Payloads { get; } = [];

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
            Calls++;
            Payloads.Add(WebSearchPayload.DeepCopyObject(payload));
            foreach (var line in _responses.Dequeue())
            {
                yield return line;
                await Task.Yield();
            }
        }
    }

    private sealed class RetrySequenceJsonUpstreamClient : IUpstreamClient
    {
        private readonly Queue<Dictionary<string, object?>> _responses;

        public RetrySequenceJsonUpstreamClient(IEnumerable<Dictionary<string, object?>> responses)
        {
            _responses = new Queue<Dictionary<string, object?>>(responses);
        }

        public int Calls { get; private set; }

        public List<Dictionary<string, object?>> Payloads { get; } = [];

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            Calls++;
            Payloads.Add(WebSearchPayload.DeepCopyObject(payload));
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new NotSupportedException("stream path is not used in this test");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class SuccessfulWebSearchToolExecutor : IWebSearchToolExecutor
    {
        public int Calls { get; private set; }

        public string CurrentMode() => WebSearchModes.Simulate;

        public Task<WebSearchToolResult> ExecuteAsync(
            string callId,
            string arguments,
            CancellationToken cancellationToken)
        {
            Calls++;
            var raw = new Dictionary<string, object?>
            {
                ["answer"] = "search answer",
                ["results"] = new List<object?>()
            };
            return Task.FromResult(new WebSearchToolResult(
                callId,
                "xlog",
                "completed",
                WebSearchPayload.JsonDumps(raw),
                raw,
                null,
                "fake",
                null,
                null,
                null,
                null,
                null,
                200,
                null));
        }
    }

    private sealed class StubWebSearchToolExecutor : IWebSearchToolExecutor
    {
        public int Calls { get; private set; }

        public string CurrentMode()
        {
            return WebSearchModes.Simulate;
        }

        public Task<WebSearchToolResult> ExecuteAsync(string callId, string arguments, CancellationToken cancellationToken)
        {
            Calls++;
            throw new NotSupportedException("this request must not execute web search");
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
            return CompleteHook?.Invoke(context) ?? Task.CompletedTask;
        }

        public Func<ProxyLogContext,Task>? CompleteHook { get; set; }
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

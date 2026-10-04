using System.Diagnostics;
using System.Text.Json;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class ProtocolConversionMatrixTests
{
    [Theory]
    [InlineData("chat", "responses")]
    [InlineData("messages", "responses")]
    [InlineData("responses", "chat")]
    [InlineData("responses", "messages")]
    public void ConvertResponse_UpstreamFailure_PreservesError(string upstreamProtocol, string clientProtocol)
    {
        var error = new D { ["type"] = "server_error", ["message"] = "summary failed" };
        var payload = new D
        {
            ["id"] = "failed_response",
            ["status"] = "failed",
            ["error"] = error
        };

        var result = ProtocolConverter.ConvertResponse(payload, clientProtocol, upstreamProtocol, "test");

        var convertedError = Assert.IsType<D>(result["error"]);
        Assert.Equal("summary failed", convertedError["message"]);
        if (clientProtocol == "responses")
        {
            Assert.Equal("failed", result["status"]);
        }
        else
        {
            Assert.False(result.ContainsKey("choices"));
            Assert.False(result.ContainsKey("stop_reason"));
        }
    }

    public static IEnumerable<object[]> ProtocolMatrix()
    {
        foreach (var entryProtocol in Protocols())
        {
            foreach (var channelProtocol in Protocols())
            {
                yield return [entryProtocol, channelProtocol];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ProtocolMatrix))]
    public async Task NonStream_AllProtocolPairs_ConvertRequestAndResponse(
        string entryProtocol,
        string channelProtocol)
    {
        var originalPayload = RequestPayload(entryProtocol, stream: false);
        var upstreamRequest = ProtocolConverter.ConvertRequest(
            originalPayload,
            entryProtocol,
            channelProtocol,
            "upstream-model");
        var upstreamResponse = ResponsePayload(channelProtocol);
        var upstream = new MatrixUpstreamClient(upstreamResponse, []);
        var logs = new MatrixLogService();
        var service = new ProxyNonStreamService(
            upstream,
            logs,
            new DisabledWebSearchToolExecutor(),
            WebSearchTestStore.Create());

        var result = await service.SendAsync(new ProxyNonStreamContext(
            Stopwatch.GetTimestamp(),
            Guid.NewGuid(),
            "req-matrix-nonstream",
            "admin",
            apiKeyId: null,
            originalPayload,
            originalPayload,
            upstreamRequest,
            entryProtocol,
            Route(channelProtocol),
            channelProtocol,
            "channel-matrix",
            "superadmin",
            "upstream-model",
            "client-model",
            120,
            RequestMetadata(entryProtocol),
            CancellationToken.None));

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Payload);
        AssertRequestShape(upstream.LastPostPayload!, channelProtocol, stream: false);
        AssertResponseShape(AsObject(result.Payload), entryProtocol, "matrix-reply");
        AssertUsage(AsObject(result.Payload), entryProtocol, inputTokens: 5, outputTokens: 2);
        Assert.Equal("client-model", JsonDictionaryValue.String(AsObject(result.Payload), "model"));
        Assert.NotNull(logs.LastContext);
        Assert.Same(upstreamResponse, logs.LastContext!.UpstreamResponse);
        Assert.Same(result.Payload, logs.LastContext.ResponsePayload);
        Assert.False(logs.LastContext.IsStream);
    }

    [Theory]
    [MemberData(nameof(ProtocolMatrix))]
    public async Task Stream_AllProtocolPairs_UseCorrectBranchAndPreserveSseLogs(
        string entryProtocol,
        string channelProtocol)
    {
        var originalPayload = RequestPayload(entryProtocol, stream: true);
        var upstreamRequest = ProtocolConverter.ConvertRequest(
            originalPayload,
            entryProtocol,
            channelProtocol,
            "upstream-model");
        var upstreamLines = StreamPayload(channelProtocol);
        var upstream = new MatrixUpstreamClient([], upstreamLines);
        var logs = new MatrixLogService();
        var writer = new MatrixStreamWriter();
        var service = new ProxyStreamService(
            upstream,
            logs,
            new DisabledWebSearchToolExecutor(),
            WebSearchTestStore.Create());

        await service.StreamAsync(new ProxyStreamContext(
            Stopwatch.GetTimestamp(),
            Guid.NewGuid(),
            "req-matrix-stream",
            "admin",
            apiKeyId: null,
            originalPayload,
            originalPayload,
            upstreamRequest,
            entryProtocol,
            Route(channelProtocol),
            channelProtocol,
            "channel-matrix",
            "superadmin",
            "upstream-model",
            "client-model",
            120,
            RequestMetadata(entryProtocol),
            writer,
            CancellationToken.None));

        Assert.True(ProtocolConverter.SupportsStreamingConversion(entryProtocol, channelProtocol));
        Assert.True(writer.Prepared);
        AssertRequestShape(upstream.LastStreamPayload!, channelProtocol, stream: true);
        AssertStreamShape(writer.Lines, entryProtocol, "matrix-reply");
        AssertStreamUsage(
            writer.Lines,
            entryProtocol,
            channelProtocol,
            inputTokens: 5,
            outputTokens: 2);
        Assert.NotNull(logs.LastContext);
        Assert.True(logs.LastContext!.IsStream);
        Assert.NotNull(logs.LastContext.UpstreamResponse);
        Assert.Equal("upstream-model", JsonDictionaryValue.String(logs.LastContext.UpstreamResponse!, "model"));
        if (entryProtocol == channelProtocol)
        {
            Assert.Null(logs.LastContext.ResponsePayload);
        }
        else
        {
            Assert.NotNull(logs.LastContext.ResponsePayload);
            Assert.Equal("client-model", JsonDictionaryValue.String(logs.LastContext.ResponsePayload!, "model"));
            AssertResponseShape(logs.LastContext.ResponsePayload!, entryProtocol, "matrix-reply");
            AssertUsage(logs.LastContext.ResponsePayload!, entryProtocol, inputTokens: 5, outputTokens: 2);
        }

        Assert.Null(logs.LastContext.Error);
    }

    public static IEnumerable<object[]> CustomToolRoundTripCases()
    {
        foreach (var channelProtocol in Protocols())
        foreach (var input in new[] { "text(1+1);", "{\"input\":\"literal\"}", " \ntext(\"中文 😀 \\\"quoted\\\"\");\n " })
            yield return [channelProtocol, input];
    }

    [Theory]
    [MemberData(nameof(CustomToolRoundTripCases))]
    public async Task ResponsesCustomTool_TwoTurns_PreservesRawInputAndResultAcrossChannels(
        string channelProtocol, string rawInput)
    {
        var request = RequestPayload(ProtocolConverter.Responses, stream: false);
        request["tools"] = new List<object?> { new D { ["type"] = "custom", ["name"] = "exec" } };
        var wrappedInput = new D { ["input"] = rawInput };
        var upstreamResponse = channelProtocol switch
        {
            ProtocolConverter.Chat => new D
            {
                ["id"] = "chat-custom", ["model"] = "upstream-model",
                ["choices"] = new List<object?> { new D
                {
                    ["index"] = 0, ["finish_reason"] = "tool_calls", ["message"] = new D
                    {
                        ["role"] = "assistant", ["tool_calls"] = new List<object?> { new D
                        {
                            ["id"] = "call-custom", ["type"] = "function", ["function"] = new D
                            { ["name"] = "exec", ["arguments"] = JsonSerializer.Serialize(wrappedInput) }
                        } }
                    }
                } }
            },
            ProtocolConverter.Messages => new D
            {
                ["id"] = "msg-custom", ["type"] = "message", ["model"] = "upstream-model",
                ["role"] = "assistant", ["stop_reason"] = "tool_use", ["content"] = new List<object?>
                { new D { ["type"] = "tool_use", ["id"] = "call-custom", ["name"] = "exec", ["input"] = wrappedInput } }
            },
            _ => new D
            {
                ["id"] = "resp-custom", ["object"] = "response", ["status"] = "completed",
                ["model"] = "upstream-model", ["output"] = new List<object?> { new D
                {
                    ["type"] = "custom_tool_call", ["id"] = "custom-item", ["call_id"] = "call-custom",
                    ["name"] = "exec", ["input"] = rawInput, ["status"] = "completed"
                } }
            }
        };
        var upstream = new MatrixUpstreamClient(upstreamResponse, []);
        var service = new ProxyNonStreamService(upstream, new MatrixLogService(),
            new DisabledWebSearchToolExecutor(), WebSearchTestStore.Create());

        async Task<D> Send(D payload)
        {
            var converted = ProtocolConverter.ConvertRequest(payload, ProtocolConverter.Responses,
                channelProtocol, "upstream-model");
            var result = await service.SendAsync(new ProxyNonStreamContext(
                Stopwatch.GetTimestamp(), Guid.NewGuid(), "req-custom-roundtrip", "admin", null,
                payload, payload, converted, ProtocolConverter.Responses, Route(channelProtocol), channelProtocol,
                "channel-matrix", "superadmin", "upstream-model", "client-model", 120,
                RequestMetadata(ProtocolConverter.Responses), CancellationToken.None));
            Assert.Equal(200, result.StatusCode);
            return AsObject(result.Payload);
        }

        var firstResponse = await Send(request);
        var call = Assert.IsType<D>(Assert.Single(JsonDictionaryValue.List(firstResponse, "output")));
        Assert.Equal("custom_tool_call", call["type"]);
        Assert.Equal("exec", call["name"]);
        Assert.Equal(rawInput, call["input"]);
        var input = JsonDictionaryValue.List(request, "input");
        input.Add(call);
        input.Add(new D { ["type"] = "custom_tool_call_output", ["call_id"] = call["call_id"], ["output"] = "EXECUTED" });
        await Send(request);

        var secondRequest = JsonSerializer.SerializeToElement(upstream.LastPostPayload);
        if (channelProtocol == ProtocolConverter.Chat)
        {
            var messages = secondRequest.GetProperty("messages").EnumerateArray().ToArray();
            var historyCall = Assert.Single(messages.Where(message => message.TryGetProperty("tool_calls", out _))
                .SelectMany(message => message.GetProperty("tool_calls").EnumerateArray()));
            using var arguments = JsonDocument.Parse(historyCall.GetProperty("function").GetProperty("arguments").GetString()!);
            Assert.Equal(rawInput, arguments.RootElement.GetProperty("input").GetString());
            Assert.Single(arguments.RootElement.EnumerateObject());
            var output = Assert.Single(messages, message => message.GetProperty("role").GetString() == "tool");
            Assert.Equal(historyCall.GetProperty("id").GetString(), output.GetProperty("tool_call_id").GetString());
            Assert.Equal("EXECUTED", output.GetProperty("content").GetString());
        }
        else if (channelProtocol == ProtocolConverter.Messages)
        {
            var blocks = secondRequest.GetProperty("messages").EnumerateArray()
                .SelectMany(message => message.GetProperty("content").EnumerateArray()).ToArray();
            var historyCall = Assert.Single(blocks, block => block.GetProperty("type").GetString() == "tool_use");
            Assert.Equal(rawInput, historyCall.GetProperty("input").GetProperty("input").GetString());
            Assert.Single(historyCall.GetProperty("input").EnumerateObject());
            var output = Assert.Single(blocks, block => block.GetProperty("type").GetString() == "tool_result");
            Assert.Equal(historyCall.GetProperty("id").GetString(), output.GetProperty("tool_use_id").GetString());
            Assert.Equal("EXECUTED", output.GetProperty("content").GetString());
        }
        else
        {
            var items = secondRequest.GetProperty("input").EnumerateArray()
                .Where(item => item.TryGetProperty("type", out _)).ToArray();
            var historyCall = Assert.Single(items, item => item.GetProperty("type").GetString() == "custom_tool_call");
            Assert.Equal(rawInput, historyCall.GetProperty("input").GetString());
            var output = Assert.Single(items, item => item.GetProperty("type").GetString() == "custom_tool_call_output");
            Assert.Equal(historyCall.GetProperty("call_id").GetString(), output.GetProperty("call_id").GetString());
            Assert.Equal("EXECUTED", output.GetProperty("output").GetString());
        }
    }

    private static IReadOnlyList<string> Protocols() =>
    [
        ProtocolConverter.Chat,
        ProtocolConverter.Messages,
        ProtocolConverter.Responses
    ];

    private static Dictionary<string, object?> RequestPayload(string protocol, bool stream)
    {
        return protocol switch
        {
            ProtocolConverter.Chat => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = "client-model",
                ["stream"] = stream,
                ["max_tokens"] = 64,
                ["stream_options"] = new Dictionary<string, object?>
                {
                    ["include_usage"] = true
                },
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["role"] = "system",
                        ["content"] = "matrix-system"
                    },
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["role"] = "user",
                        ["content"] = "matrix-question"
                    }
                }
            },
            ProtocolConverter.Messages => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = "client-model",
                ["stream"] = stream,
                ["max_tokens"] = 64,
                ["system"] = "matrix-system",
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["role"] = "user",
                        ["content"] = "matrix-question"
                    }
                }
            },
            ProtocolConverter.Responses => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = "client-model",
                ["stream"] = stream,
                ["max_output_tokens"] = 64,
                ["instructions"] = "matrix-system",
                ["input"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["role"] = "user",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["type"] = "input_text",
                                ["text"] = "matrix-question"
                            }
                        }
                    }
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
    }

    private static Dictionary<string, object?> ResponsePayload(string protocol)
    {
        return protocol switch
        {
            ProtocolConverter.Chat => ParseObject("""
                {
                  "id":"chat-matrix",
                  "object":"chat.completion",
                  "model":"upstream-model",
                  "choices":[{"index":0,"message":{"role":"assistant","content":"matrix-reply"},"finish_reason":"stop"}],
                  "usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}
                }
                """),
            ProtocolConverter.Messages => ParseObject("""
                {
                  "id":"msg-matrix",
                  "type":"message",
                  "role":"assistant",
                  "model":"upstream-model",
                  "content":[{"type":"text","text":"matrix-reply"}],
                  "stop_reason":"end_turn",
                  "stop_sequence":null,
                  "usage":{"input_tokens":5,"output_tokens":2}
                }
                """),
            ProtocolConverter.Responses => ParseObject("""
                {
                  "id":"resp-matrix",
                  "object":"response",
                  "status":"completed",
                  "model":"upstream-model",
                  "output":[{"id":"msg-matrix","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"matrix-reply"}]}],
                  "usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}
                }
                """),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
    }

    private static IReadOnlyList<string> StreamPayload(string protocol)
    {
        return protocol switch
        {
            ProtocolConverter.Chat =>
            [
                "data: {\"id\":\"chat-matrix\",\"object\":\"chat.completion.chunk\",\"model\":\"upstream-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"matrix-reply\"},\"finish_reason\":null}]}\n\n",
                "data: {\"id\":\"chat-matrix\",\"object\":\"chat.completion.chunk\",\"model\":\"upstream-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}\n\n",
                "data: [DONE]\n\n"
            ],
            ProtocolConverter.Messages =>
            [
                "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-matrix\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"upstream-model\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":5,\"output_tokens\":0}}}\n\n",
                "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n",
                "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"matrix-reply\"}}\n\n",
                "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n",
                "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":2}}\n\n",
                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
            ],
            ProtocolConverter.Responses =>
            [
                "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp-matrix\",\"object\":\"response\",\"status\":\"in_progress\",\"model\":\"upstream-model\",\"output\":[]}}\n\n",
                "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"msg-matrix\",\"type\":\"message\",\"status\":\"in_progress\",\"role\":\"assistant\",\"content\":[]}}\n\n",
                "event: response.content_part.added\ndata: {\"type\":\"response.content_part.added\",\"item_id\":\"msg-matrix\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"\"}}\n\n",
                "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg-matrix\",\"output_index\":0,\"content_index\":0,\"delta\":\"matrix-reply\"}\n\n",
                "event: response.output_text.done\ndata: {\"type\":\"response.output_text.done\",\"item_id\":\"msg-matrix\",\"output_index\":0,\"content_index\":0,\"text\":\"matrix-reply\"}\n\n",
                "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"msg-matrix\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"matrix-reply\"}]}}\n\n",
                "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-matrix\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"upstream-model\",\"output\":[{\"id\":\"msg-matrix\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"matrix-reply\"}]}],\"usage\":{\"input_tokens\":5,\"output_tokens\":2,\"total_tokens\":7}}}\n\n",
                "data: [DONE]\n\n"
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
    }

    private static void AssertRequestShape(
        IReadOnlyDictionary<string, object?> payload,
        string protocol,
        bool stream)
    {
        Assert.Equal("upstream-model", JsonDictionaryValue.String(payload, "model"));
        Assert.Equal(stream, Assert.IsType<bool>(payload["stream"]));
        var json = JsonSerializer.Serialize(payload);
        Assert.Contains("matrix-question", json, StringComparison.Ordinal);
        Assert.Contains("matrix-system", json, StringComparison.Ordinal);

        switch (protocol)
        {
            case ProtocolConverter.Chat:
                Assert.True(payload.ContainsKey("messages"));
                Assert.Equal(64, Convert.ToInt32(payload["max_tokens"]));
                Assert.False(payload.ContainsKey("input"));
                Assert.False(payload.ContainsKey("system"));
                break;
            case ProtocolConverter.Messages:
                Assert.True(payload.ContainsKey("messages"));
                Assert.True(payload.ContainsKey("system"));
                Assert.Equal(64, Convert.ToInt32(payload["max_tokens"]));
                Assert.False(payload.ContainsKey("input"));
                break;
            case ProtocolConverter.Responses:
                Assert.True(payload.ContainsKey("input"));
                Assert.True(payload.ContainsKey("instructions"));
                Assert.Equal(64, Convert.ToInt32(payload["max_output_tokens"]));
                Assert.False(payload.ContainsKey("messages"));
                break;
        }
    }

    private static void AssertResponseShape(
        IReadOnlyDictionary<string, object?> payload,
        string protocol,
        string expectedText)
    {
        var json = JsonSerializer.Serialize(payload);
        Assert.Contains(expectedText, json, StringComparison.Ordinal);
        switch (protocol)
        {
            case ProtocolConverter.Chat:
                Assert.Equal("chat.completion", JsonDictionaryValue.String(payload, "object"));
                Assert.True(payload.ContainsKey("choices"));
                break;
            case ProtocolConverter.Messages:
                Assert.Equal("message", JsonDictionaryValue.String(payload, "type"));
                Assert.True(payload.ContainsKey("content"));
                Assert.Equal("end_turn", JsonDictionaryValue.String(payload, "stop_reason"));
                break;
            case ProtocolConverter.Responses:
                Assert.Equal("response", JsonDictionaryValue.String(payload, "object"));
                Assert.True(payload.ContainsKey("output"));
                Assert.Equal("completed", JsonDictionaryValue.String(payload, "status"));
                break;
        }
    }

    private static void AssertStreamShape(
        IReadOnlyList<string> lines,
        string protocol,
        string expectedText)
    {
        var body = string.Concat(lines);
        Assert.Contains(expectedText, body, StringComparison.Ordinal);
        switch (protocol)
        {
            case ProtocolConverter.Chat:
                Assert.Contains("chat.completion.chunk", body, StringComparison.Ordinal);
                Assert.Equal("data: [DONE]", lines[^1].Trim());
                break;
            case ProtocolConverter.Messages:
                Assert.Contains("event: message_start", body, StringComparison.Ordinal);
                Assert.Contains("event: message_stop", body, StringComparison.Ordinal);
                break;
            case ProtocolConverter.Responses:
                Assert.Contains("event: response.created", body, StringComparison.Ordinal);
                Assert.Contains("event: response.completed", body, StringComparison.Ordinal);
                Assert.Equal("data: [DONE]", lines[^1].Trim());
                break;
        }
    }

    private static void AssertUsage(
        IReadOnlyDictionary<string, object?> payload,
        string protocol,
        int inputTokens,
        int outputTokens)
    {
        var usage = AsDictionary(payload["usage"]);
        Assert.NotNull(usage);
        switch (protocol)
        {
            case ProtocolConverter.Chat:
                Assert.Equal(inputTokens, ToInt32(usage["prompt_tokens"]));
                Assert.Equal(outputTokens, ToInt32(usage["completion_tokens"]));
                Assert.Equal(inputTokens + outputTokens, ToInt32(usage["total_tokens"]));
                break;
            case ProtocolConverter.Messages:
            case ProtocolConverter.Responses:
                Assert.Equal(inputTokens, ToInt32(usage["input_tokens"]));
                Assert.Equal(outputTokens, ToInt32(usage["output_tokens"]));
                break;
        }
    }

    private static void AssertStreamUsage(
        IReadOnlyList<string> lines,
        string protocol,
        string channelProtocol,
        int inputTokens,
        int outputTokens)
    {
        var payloads = lines
            .SelectMany(ParseSsePayloads)
            .ToList();

        switch (protocol)
        {
            case ProtocolConverter.Chat:
            {
                var usagePayload = Assert.Single(payloads, payload =>
                    payload.TryGetValue("usage", out var value)
                    && AsDictionary(value) is not null);
                AssertUsage(usagePayload, protocol, inputTokens, outputTokens);
                break;
            }
            case ProtocolConverter.Messages:
            {
                var start = Assert.Single(payloads, payload =>
                    JsonDictionaryValue.String(payload, "type") == "message_start");
                var message = AsDictionary(start["message"]);
                Assert.NotNull(message);
                var startUsage = AsDictionary(message["usage"]);
                Assert.NotNull(startUsage);
                var expectedStartInputTokens = channelProtocol == ProtocolConverter.Messages
                    ? inputTokens
                    : 0;
                Assert.Equal(expectedStartInputTokens, ToInt32(startUsage["input_tokens"]));

                var delta = Assert.Single(payloads, payload =>
                    JsonDictionaryValue.String(payload, "type") == "message_delta");
                var deltaUsage = AsDictionary(delta["usage"]);
                Assert.NotNull(deltaUsage);
                Assert.Equal(outputTokens, ToInt32(deltaUsage["output_tokens"]));
                break;
            }
            case ProtocolConverter.Responses:
            {
                var completed = Assert.Single(payloads, payload =>
                    JsonDictionaryValue.String(payload, "type") == "response.completed");
                var response = AsDictionary(completed["response"]);
                Assert.NotNull(response);
                AssertUsage(response, protocol, inputTokens, outputTokens);
                break;
            }
        }
    }

    private static IEnumerable<Dictionary<string, object?>> ParseSsePayloads(string block)
    {
        var normalized = block.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            if (line.StartsWith("data: {", StringComparison.Ordinal))
            {
                yield return ParseObject(line[6..]);
            }
        }
    }

    private static Dictionary<string, object?>? AsDictionary(object? value)
    {
        if (value is Dictionary<string, object?> dictionary)
        {
            return dictionary;
        }

        if (value is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            return ParseObject(element.GetRawText());
        }

        return null;
    }

    private static int ToInt32(object? value)
    {
        return value is JsonElement element
            ? element.GetInt32()
            : Convert.ToInt32(value);
    }

    private static ProxyRouteDto Route(string protocol)
    {
        return new ProxyRouteDto(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = "channel-matrix",
                ["type"] = protocol
            },
            "client-model",
            "upstream-model",
            supportsImage: false,
            matchedModelMapping: false);
    }

    private static ProxyRequestMetadata RequestMetadata(string protocol) => new(
        "POST",
        $"/v1/{protocol}",
        "127.0.0.1",
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static Dictionary<string, object?> ParseObject(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
            ?? throw new InvalidOperationException("matrix JSON must be an object");
    }

    private static Dictionary<string, object?> AsObject(object? value)
    {
        return Assert.IsType<Dictionary<string, object?>>(value);
    }

    private sealed class MatrixUpstreamClient : IUpstreamClient
    {
        private readonly Dictionary<string, object?> _response;
        private readonly IReadOnlyList<string> _streamLines;

        public MatrixUpstreamClient(
            Dictionary<string, object?> response,
            IReadOnlyList<string> streamLines)
        {
            _response = response;
            _streamLines = streamLines;
        }

        public IReadOnlyDictionary<string, object?>? LastPostPayload { get; private set; }

        public IReadOnlyDictionary<string, object?>? LastStreamPayload { get; private set; }

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            LastPostPayload = payload;
            return Task.FromResult(_response);
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastStreamPayload = payload;
            foreach (var block in _streamLines)
            {
                foreach (var line in SplitSseLines(block))
                {
                    yield return line;
                    await Task.Yield();
                }
            }
        }

        private static IEnumerable<string> SplitSseLines(string block)
        {
            var normalized = block.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var parts = normalized.Split('\n');
            var count = normalized.EndsWith('\n') ? parts.Length - 1 : parts.Length;
            for (var index = 0; index < count; index++)
            {
                yield return parts[index];
            }
        }
    }

    private sealed class MatrixStreamWriter : IProxyStreamWriter
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
                Prepared = true;
                Lines.Add(line);
                if (metrics.TtftMs is null && countsForTtft(line))
                {
                    metrics.TtftMs = elapsedMilliseconds();
                }
            }

            return metrics;
        }
    }

    private sealed class MatrixLogService : IProxyLogService
    {
        public ProxyLogContext? LastContext { get; private set; }

        public Guid CreateQueuedLog(ProxyRequestLogQueuedContext context) => Guid.NewGuid();

        public void MarkProcessing(Guid requestLogId, ProxyRequestLogProcessingContext context)
        {
        }

        public Task CompleteLogAsync(
            Guid requestLogId,
            ProxyLogContext context,
            ProxyRequestMetadata request)
        {
            LastContext = context;
            return Task.CompletedTask;
        }

        public Task<Guid> WriteLogAsync(
            ProxyLogContext context,
            ProxyRequestMetadata request)
        {
            LastContext = context;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<Guid> WriteLogAsync(ProxyRequestLogContext context)
            => Task.FromResult(Guid.NewGuid());
    }

    private sealed class DisabledWebSearchToolExecutor : IWebSearchToolExecutor
    {
        public string CurrentMode() => "convert";

        public Task<WebSearchToolResult> ExecuteAsync(string callId, string arguments, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}

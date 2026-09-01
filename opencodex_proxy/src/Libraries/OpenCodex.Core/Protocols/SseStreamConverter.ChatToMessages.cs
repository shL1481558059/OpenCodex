using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    // 上游协议: Chat (OpenAI /v1/chat/completions SSE)
    // 下游协议: Messages (Anthropic /v1/messages SSE)
    //
    // 输入: Chat 流式 chunk `choices[].delta`，含 content / tool_calls / refusal，
    //       思维链由 ChatReasoningText 统一取自 reasoning_content 或 reasoning / reasoning_details
    // 输出: Anthropic Messages 事件流 message_start -> content_block_start/delta/stop -> message_delta -> message_stop
    //
    // 已知限制（与现有非流式 ConvertResponse 行为一致，不在流式侧隐藏）：
    //  - thinking 块无 signature_delta / redacted_thinking：上游 Chat 的思维链字段不携带签名，
    //    多轮历史中 thinking 不可验证；不伪造签名。
    //  - Chat usage 通常仅在末尾出现，message_start.usage.input_tokens 因此保持 0；
    //    output_tokens 在 message_delta 中按上游 usage 报告，避免为回填 usage 而破坏实时流式语义。
    //  - 工具名直接透传 Chat 的 function.name（与非流式 CanonicalToMessagesResponse 一致），
    //    arguments 原样作为 input_json_delta 的 partial_json 流出，由下游自行拼装为 JSON。
    public static async IAsyncEnumerable<string> ChatToMessagesEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ChatToMessagesEvents(
            upstreamLines,
            model,
            result,
            SkipToolNames: null,
            SkipMessageStart: false,
            cancellationToken))
        {
            yield return line;
        }
    }

    public static async IAsyncEnumerable<string> ChatToMessagesEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        IReadOnlySet<string>? SkipToolNames,
        bool SkipMessageStart,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messageId = $"msg_{Guid.NewGuid():N}";
        var responseModel = model;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var finishReason = "stop";
        var usage = new Dictionary<string, object?>(StringComparer.Ordinal);
        var upstreamResponseAccumulator = new ChatStreamResponseAccumulator(
            new StreamCaptureBudget(int.MaxValue, int.MaxValue));

        var nextBlockIndex = 0;
        var openBlockIndex = (int?)null; // 当前仍在流的 block，需在切换/结束时发送 content_block_stop
        var thinkingIndex = (int?)null;
        var thinkingStarted = false;
        var textIndex = (int?)null;
        var textStarted = false;

        // chat tool 索引 -> Anthropic block 信息
        var toolAggregates = new SortedDictionary<int, ToolCallAggregate>();
        // Chat 允许多个 tool_calls 的 arguments delta 交错出现；Anthropic content block
        // 一旦 stop 后不能再次追加 delta。因此保留原始片段，读取完成后按工具顺序逐块输出。
        var toolArgumentDeltas = new Dictionary<int, List<string>>();

        string Emit(string eventName, Dictionary<string, object?> payload)
        {
            payload["type"] = eventName;
            return $"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, JsonOptions)}\n\n";
        }

        int AllocateBlockIndex()
        {
            return nextBlockIndex++;
        }

        void CloseOpenBlock(List<string> output)
        {
            if (openBlockIndex is null)
            {
                return;
            }

            output.Add(Emit(
                "content_block_stop",
                new Dictionary<string, object?>
                {
                    ["index"] = openBlockIndex
                }));
            openBlockIndex = null;
        }

        List<string> EnsureThinkingStarted()
        {
            if (thinkingStarted)
            {
                return [];
            }

            thinkingStarted = true;
            thinkingIndex = AllocateBlockIndex();
            var output = new List<string>();
            CloseOpenBlock(output);
            openBlockIndex = thinkingIndex;
            output.Add(Emit(
                "content_block_start",
                new Dictionary<string, object?>
                {
                    ["index"] = thinkingIndex,
                    ["content_block"] = new Dictionary<string, object?>
                    {
                        ["type"] = "thinking",
                        ["thinking"] = string.Empty
                    }
                }));
            return output;
        }

        List<string> EnsureTextStarted()
        {
            if (textStarted)
            {
                return [];
            }

            textStarted = true;
            textIndex = AllocateBlockIndex();
            var output = new List<string>();
            CloseOpenBlock(output);
            openBlockIndex = textIndex;
            output.Add(Emit(
                "content_block_start",
                new Dictionary<string, object?>
                {
                    ["index"] = textIndex,
                    ["content_block"] = new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = string.Empty
                    }
                }));
            return output;
        }

        // 提前启动上游HTTP请求，避免延迟（与现有转换器一致的 TTFT 策略）
        var enumerator = ParseEvents(upstreamLines, cancellationToken).GetAsyncEnumerator(cancellationToken);

        if (!SkipMessageStart)
        {
            yield return Emit(
                "message_start",
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["id"] = messageId,
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = new List<object?>(),
                        ["model"] = responseModel,
                        ["stop_reason"] = null,
                        ["stop_sequence"] = null,
                        ["usage"] = new Dictionary<string, object?>
                        {
                            ["input_tokens"] = 0,
                            ["output_tokens"] = 0
                        }
                    }
                });
        }

        while (await enumerator.MoveNextAsync())
        {
            var sseEvent = enumerator.Current;
            upstreamResponseAccumulator.Accept(sseEvent);
            if (sseEvent.Data is string dataText && dataText == "[DONE]")
            {
                break;
            }

            if (sseEvent.Data is not Dictionary<string, object?> payload)
            {
                continue;
            }

            responseModel = model ?? StringValue(payload, "model", responseModel);
            var upstreamError = GetValue(payload, "error");
            if (upstreamError is not null
                || string.Equals(StringValue(payload, "type", string.Empty), "error", StringComparison.Ordinal))
            {
                result.UpstreamResponse = upstreamResponseAccumulator.BuildResponse() ?? payload;
                yield return Emit(
                    "error",
                    new Dictionary<string, object?>
                    {
                        ["error"] = upstreamError ?? payload
                    });
                yield break;
            }

            if (TryAsObject(GetValue(payload, "usage"), out var usageObject))
            {
                usage = usageObject;
            }

            if (!TryAsList(GetValue(payload, "choices"), out var choices))
            {
                continue;
            }

            foreach (var choiceValue in choices)
            {
                if (!TryAsObject(choiceValue, out var choice))
                {
                    continue;
                }

                finishReason = StringValue(choice, "finish_reason", finishReason);
                if (string.IsNullOrEmpty(finishReason) || finishReason == "null")
                {
                    finishReason = "stop";
                }

                if (!TryAsObject(GetValue(choice, "delta"), out var delta))
                {
                    continue;
                }

                var reasoningText = ChatReasoningText.Extract(delta);
                if (reasoningText.Length > 0)
                {
                    foreach (var line in EnsureThinkingStarted())
                    {
                        yield return line;
                    }

                    yield return Emit(
                        "content_block_delta",
                        new Dictionary<string, object?>
                        {
                            ["index"] = thinkingIndex,
                            ["delta"] = new Dictionary<string, object?>
                            {
                                ["type"] = "thinking_delta",
                                ["thinking"] = reasoningText
                            }
                        });
                }

                var text = StringValue(delta, "content", string.Empty);
                if (text.Length > 0)
                {
                    foreach (var line in EnsureTextStarted())
                    {
                        yield return line;
                    }

                    yield return Emit(
                        "content_block_delta",
                        new Dictionary<string, object?>
                        {
                            ["index"] = textIndex,
                            ["delta"] = new Dictionary<string, object?>
                            {
                                ["type"] = "text_delta",
                                ["text"] = text
                            }
                        });
                }

                var refusal = StringValue(delta, "refusal", string.Empty);
                if (refusal.Length > 0)
                {
                    // Anthropic 没有 refusal 类型，按非流式处理方式归并到文本块
                    foreach (var line in EnsureTextStarted())
                    {
                        yield return line;
                    }

                    yield return Emit(
                        "content_block_delta",
                        new Dictionary<string, object?>
                        {
                            ["index"] = textIndex,
                            ["delta"] = new Dictionary<string, object?>
                            {
                                ["type"] = "text_delta",
                                ["text"] = refusal
                            }
                        });
                }

                if (!TryAsList(GetValue(delta, "tool_calls"), out var rawToolCalls))
                {
                    continue;
                }

                foreach (var rawToolCall in rawToolCalls)
                {
                    if (!TryAsObject(rawToolCall, out var toolCall))
                    {
                        continue;
                    }

                    var index = ToInt(GetValue(toolCall, "index"));
                    if (!toolAggregates.TryGetValue(index, out var aggregate))
                    {
                        aggregate = new ToolCallAggregate();
                        toolAggregates[index] = aggregate;
                    }

                    var id = StringValue(toolCall, "id", string.Empty);
                    if (id.Length > 0)
                    {
                        aggregate.Id = id;
                    }

                    var type = StringValue(toolCall, "type", string.Empty);
                    if (type.Length > 0)
                    {
                        aggregate.Type = type;
                    }

                    if (TryAsObject(GetValue(toolCall, "function"), out var function))
                    {
                        var name = StringValue(function, "name", string.Empty);
                        if (name.Length > 0)
                        {
                            aggregate.Name = name;
                        }

                        var arguments = StringValue(function, "arguments", string.Empty);
                        if (arguments.Length > 0)
                        {
                            aggregate.Arguments += arguments;
                            if (!toolArgumentDeltas.TryGetValue(index, out var argumentDeltas))
                            {
                                argumentDeltas = [];
                                toolArgumentDeltas[index] = argumentDeltas;
                            }

                            argumentDeltas.Add(arguments);
                        }
                    }
                }
            }
        }

        foreach (var (index, aggregate) in toolAggregates.ToList())
        {
            var callId = string.IsNullOrEmpty(aggregate.Id) ? $"call_{Guid.NewGuid():N}" : aggregate.Id;
            var arguments = aggregate.Arguments.Length > 0 ? aggregate.Arguments : "{}";
            aggregate.Id = callId;
            aggregate.Arguments = arguments;
            toolAggregates[index] = aggregate;
        }

        result.UpstreamResponse = upstreamResponseAccumulator.BuildResponse()
            ?? BuildEmptyChatCompletion(responseModel, createdAt, finishReason, usage);

        // 关闭最后一个仍打开的块
        var closingOutput = new List<string>();
        CloseOpenBlock(closingOutput);
        foreach (var line in closingOutput)
        {
            yield return line;
        }

        // Anthropic 要求每个 content block 的 start/delta/stop 连续且不可重开。
        // 即使 Chat 上游交错发送多个工具参数，也在这里按 tool index 输出合法的顺序块。
        foreach (var (toolIndex, aggregate) in toolAggregates)
        {
            if (string.IsNullOrEmpty(aggregate.Id)
                || string.IsNullOrEmpty(aggregate.Name)
                || SkipToolNames?.Contains(aggregate.Name) is true)
            {
                continue;
            }

            var blockIndex = AllocateBlockIndex();
            yield return Emit(
                "content_block_start",
                new Dictionary<string, object?>
                {
                    ["index"] = blockIndex,
                    ["content_block"] = new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = aggregate.Id,
                        ["name"] = aggregate.Name,
                        ["input"] = new Dictionary<string, object?>()
                    }
                });

            if (toolArgumentDeltas.TryGetValue(toolIndex, out var argumentDeltas))
            {
                foreach (var argumentDelta in argumentDeltas)
                {
                    yield return Emit(
                        "content_block_delta",
                        new Dictionary<string, object?>
                        {
                            ["index"] = blockIndex,
                            ["delta"] = new Dictionary<string, object?>
                            {
                                ["type"] = "input_json_delta",
                                ["partial_json"] = argumentDelta
                            }
                        });
                }
            }

            yield return Emit(
                "content_block_stop",
                new Dictionary<string, object?>
                {
                    ["index"] = blockIndex
                });
        }

        var outputTokens = ToInt(GetValue(usage, "completion_tokens"));
        var stopReason = FinishReasonToMessagesStopReason(finishReason);
        yield return Emit(
            "message_delta",
            new Dictionary<string, object?>
            {
                ["delta"] = new Dictionary<string, object?>
                {
                    ["stop_reason"] = stopReason,
                    ["stop_sequence"] = null
                },
                ["usage"] = new Dictionary<string, object?>
                {
                    ["output_tokens"] = outputTokens
                }
            });

        yield return Emit(
            "message_stop",
            new Dictionary<string, object?>());
    }

    private static string FinishReasonToMessagesStopReason(string finishReason)
    {
        return finishReason switch
        {
            "stop" => "end_turn",
            "length" => "max_tokens",
            "tool_calls" or "function_call" => "tool_use",
            _ => "end_turn"
        };
    }
}

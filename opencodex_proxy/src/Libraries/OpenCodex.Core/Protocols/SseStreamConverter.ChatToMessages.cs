using System.Text;
using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    // 上游协议: Chat (OpenAI /v1/chat/completions SSE)
    // 下游协议: Messages (Anthropic /v1/messages SSE)
    //
    // 输入: Chat 流式 chunk `choices[].delta`，含 content / reasoning_content / tool_calls / refusal
    // 输出: Anthropic Messages 事件流 message_start -> content_block_start/delta/stop -> message_delta -> message_stop
    //
    // 已知限制（与现有非流式 ConvertResponse 行为一致，不在流式侧隐藏）：
    //  - thinking 块无 signature_delta / redacted_thinking：上游 Chat 的 reasoning_content 不携带签名，
    //    多轮历史中 thinking 不可验证；不伪造签名。
    //  - message_start.usage.input_tokens 固定为 0：代理未向上游请求设置 stream_options.include_usage，
    //    usage 通常仅在末尾出现且无法回填 message_start；output_tokens 在 message_delta 中按上游 usage 报告。
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
        var completionId = string.Empty;
        object? completionCreated = null;
        var finishReason = "stop";
        var usage = new Dictionary<string, object?>(StringComparer.Ordinal);

        var textParts = new List<string>();
        var refusalParts = new List<string>();
        var reasoningParts = new List<string>();

        var nextBlockIndex = 0;
        var openBlockIndex = (int?)null; // 当前仍在流的 block，需在切换/结束时发送 content_block_stop
        var thinkingIndex = (int?)null;
        var thinkingStarted = false;
        var textIndex = (int?)null;
        var textStarted = false;

        // chat tool 索引 -> Anthropic block 信息
        var toolAggregates = new SortedDictionary<int, ToolCallAggregate>();
        var toolBlockIndex = new Dictionary<int, int>();
        var toolBlockStarted = new Dictionary<int, bool>();
        var toolStreamedLength = new Dictionary<int, int>();

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
            if (sseEvent.Data is string dataText && dataText == "[DONE]")
            {
                break;
            }

            if (sseEvent.Data is not Dictionary<string, object?> payload)
            {
                continue;
            }

            completionId = StringValue(payload, "id", completionId);
            completionCreated = GetValue(payload, "created") ?? completionCreated;
            responseModel = model ?? StringValue(payload, "model", responseModel);
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

                var reasoningText = StringValue(delta, "reasoning_content", string.Empty);
                if (reasoningText.Length > 0)
                {
                    foreach (var line in EnsureThinkingStarted())
                    {
                        yield return line;
                    }

                    reasoningParts.Add(reasoningText);
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

                    textParts.Add(text);
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

                    textParts.Add(refusal);
                    refusalParts.Add(refusal);
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
                        }
                    }

                    if (string.IsNullOrEmpty(aggregate.Id) || string.IsNullOrEmpty(aggregate.Name))
                    {
                        continue;
                    }

                    if (SkipToolNames?.Contains(aggregate.Name) is true)
                    {
                        continue;
                    }

                    // 首次见到该工具：先收尾上一个块，再开 tool_use 块
                    if (!toolBlockStarted.TryGetValue(index, out var started) || !started)
                    {
                        toolBlockStarted[index] = true;
                        toolBlockIndex[index] = AllocateBlockIndex();
                        var openOutput = new List<string>();
                        CloseOpenBlock(openOutput);
                        foreach (var line in openOutput)
                        {
                            yield return line;
                        }

                        openBlockIndex = toolBlockIndex[index];
                        yield return Emit(
                            "content_block_start",
                            new Dictionary<string, object?>
                            {
                                ["index"] = toolBlockIndex[index],
                                ["content_block"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "tool_use",
                                    ["id"] = aggregate.Id,
                                    ["name"] = aggregate.Name,
                                    ["input"] = new Dictionary<string, object?>()
                                }
                            });
                    }

                    var streamed = toolStreamedLength.TryGetValue(index, out var s) ? s : 0;
                    if (aggregate.Arguments.Length <= streamed)
                    {
                        continue;
                    }

                    var deltaText = aggregate.Arguments[streamed..];
                    toolStreamedLength[index] = aggregate.Arguments.Length;
                    yield return Emit(
                        "content_block_delta",
                        new Dictionary<string, object?>
                        {
                            ["index"] = toolBlockIndex[index],
                            ["delta"] = new Dictionary<string, object?>
                            {
                                ["type"] = "input_json_delta",
                                ["partial_json"] = deltaText
                            }
                        });
                }
            }
        }

        // 收尾上游响应记录（Chat 格式，供 ProxyStreamService 的 ConvertResponse 转为 Messages 记录用）
        var combinedText = string.Concat(textParts);
        var combinedReasoning = string.Concat(reasoningParts);
        var reconstructedToolCalls = new List<object?>();
        foreach (var (index, aggregate) in toolAggregates.ToList())
        {
            var callId = string.IsNullOrEmpty(aggregate.Id) ? $"call_{Guid.NewGuid():N}" : aggregate.Id;
            var arguments = aggregate.Arguments.Length > 0 ? aggregate.Arguments : "{}";
            reconstructedToolCalls.Add(new Dictionary<string, object?>
            {
                ["id"] = callId,
                ["type"] = aggregate.Type,
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = aggregate.Name,
                    ["arguments"] = arguments
                }
            });
            aggregate.Id = callId;
            aggregate.Arguments = arguments;
            toolAggregates[index] = aggregate;
        }

        result.UpstreamResponse = new Dictionary<string, object?>
        {
            ["id"] = completionId.Length > 0 ? completionId : $"chatcmpl_{Guid.NewGuid():N}",
            ["object"] = "chat.completion",
            ["created"] = completionCreated ?? createdAt,
            ["model"] = responseModel,
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = combinedText,
                        ["tool_calls"] = reconstructedToolCalls,
                        ["reasoning_content"] = combinedReasoning,
                        ["refusal"] = string.Concat(refusalParts)
                    },
                    ["finish_reason"] = finishReason
                }
            },
            ["usage"] = usage
        };

        // 关闭最后一个仍打开的块
        var closingOutput = new List<string>();
        CloseOpenBlock(closingOutput);
        foreach (var line in closingOutput)
        {
            yield return line;
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

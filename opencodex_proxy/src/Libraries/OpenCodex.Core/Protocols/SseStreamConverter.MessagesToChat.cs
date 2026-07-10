using System.Text;
using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    // 上游协议: Messages (Anthropic /v1/messages SSE)
    // 下游协议: Chat (OpenAI /v1/chat/completions SSE)
    //
    // 输入: Anthropic 事件流 message_start / content_block_start/delta/stop / message_delta / message_stop
    // 输出: OpenAI chat.completion.chunk 流，末尾 data: [DONE]
    //
    // 已知限制（与现有非流式 ConvertResponse 行为一致，不在流式侧隐藏）：
    //  - thinking 的 signature_delta / redacted_thinking 会被丢弃：Chat 协议的 reasoning_content 仅是纯文本，
    //    没有 Anthropic 的签名机制；多轮历史中 thinking 不可验证。不伪造签名。
    //  - 工具名直接透传 Anthropic tool_use.name（与非流式 ChatResponseToCanonical 一致），
    //    tool input 的 partial_json 原样作为 function.arguments 片段流出。
    public static async IAsyncEnumerable<string> MessagesToChatEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in MessagesToChatEvents(
            upstreamLines,
            model,
            result,
            SkipToolNames: null,
            IncludeUsage: true,
            cancellationToken))
        {
            yield return line;
        }
    }

    public static async IAsyncEnumerable<string> MessagesToChatEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        IReadOnlySet<string>? SkipToolNames,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in MessagesToChatEvents(
            upstreamLines,
            model,
            result,
            SkipToolNames,
            IncludeUsage: true,
            cancellationToken))
        {
            yield return line;
        }
    }

    public static async IAsyncEnumerable<string> MessagesToChatEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        IReadOnlySet<string>? SkipToolNames,
        bool IncludeUsage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completionId = $"chatcmpl_{Guid.NewGuid():N}";
        var responseModel = model ?? string.Empty;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usage = new Dictionary<string, object?>(StringComparer.Ordinal);
        var stopReason = "end_turn";

        var contentBlocks = new SortedDictionary<int, Dictionary<string, object?>>();
        var upstreamResponseAccumulator = new MessagesStreamResponseAccumulator(
            new StreamCaptureBudget(int.MaxValue, int.MaxValue));

        var firstRoleEmitted = false;
        var chatToolIndexByBlock = new Dictionary<int, int>();
        var nextChatToolIndex = 0;

        string EmitChunk(List<object?> choices)
        {
            var chunk = new Dictionary<string, object?>
            {
                ["id"] = completionId,
                ["object"] = "chat.completion.chunk",
                ["created"] = createdAt,
                ["model"] = responseModel,
                ["choices"] = choices
            };
            return $"data: {JsonSerializer.Serialize(chunk, JsonOptions)}\n\n";
        }

        List<string> EnsureRoleChunk()
        {
            if (firstRoleEmitted)
            {
                return [];
            }

            firstRoleEmitted = true;
            return
            [
                EmitChunk(new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["delta"] = new Dictionary<string, object?> { ["role"] = "assistant" },
                        ["finish_reason"] = null
                    }
                })
            ];
        }

        // 提前启动上游HTTP请求，避免延迟（与现有转换器一致的 TTFT 策略）
        var enumerator = ParseEvents(upstreamLines, cancellationToken).GetAsyncEnumerator(cancellationToken);

        while (await enumerator.MoveNextAsync())
        {
            var sseEvent = enumerator.Current;
            upstreamResponseAccumulator.Accept(sseEvent);
            if (!TryAsObject(sseEvent.Data, out var payload))
            {
                continue;
            }

            var eventType = StringValue(payload, "type", sseEvent.EventName);
            if (eventType == "error")
            {
                result.UpstreamResponse = upstreamResponseAccumulator.BuildResponse() ?? payload;
                var errorPayload = new Dictionary<string, object?>
                {
                    ["error"] = GetValue(payload, "error") ?? payload
                };
                yield return $"data: {JsonSerializer.Serialize(errorPayload, JsonOptions)}\n\n";
                yield break;
            }

            if (eventType == "message_start")
            {
                if (TryAsObject(GetValue(payload, "message"), out var message))
                {
                    responseModel = model ?? StringValue(message, "model", responseModel);
                    if (TryAsObject(GetValue(message, "usage"), out var messageUsage))
                    {
                        usage = messageUsage;
                    }
                }

                continue;
            }

            if (eventType == "content_block_start")
            {
                var index = ToInt(GetValue(payload, "index"));
                if (TryAsObject(GetValue(payload, "content_block"), out var block))
                {
                    var blockType = StringValue(block, "type", string.Empty);
                    contentBlocks[index] = new Dictionary<string, object?>(block, StringComparer.Ordinal);

                    if (blockType == "tool_use")
                    {
                        var toolName = StringValue(block, "name", string.Empty);
                        if (SkipToolNames?.Contains(toolName) is true)
                        {
                            continue;
                        }

                        var chatIndex = nextChatToolIndex++;
                        chatToolIndexByBlock[index] = chatIndex;
                        foreach (var line in EnsureRoleChunk())
                        {
                            yield return line;
                        }

                        // tool_call 起始 chunk：含 id / type / function.name，arguments 为空（后续由 input_json_delta 增量填充）
                        yield return EmitChunk(new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["index"] = 0,
                                ["delta"] = new Dictionary<string, object?>
                                {
                                    ["tool_calls"] = new List<object?>
                                    {
                                        new Dictionary<string, object?>
                                        {
                                            ["index"] = chatIndex,
                                            ["id"] = GetValue(block, "id"),
                                            ["type"] = "function",
                                            ["function"] = new Dictionary<string, object?>
                                            {
                                                ["name"] = GetValue(block, "name"),
                                                ["arguments"] = string.Empty
                                            }
                                        }
                                    }
                                },
                                ["finish_reason"] = null
                            }
                        });
                    }
                }

                continue;
            }

            if (eventType == "content_block_delta")
            {
                var index = ToInt(GetValue(payload, "index"));
                if (!TryAsObject(GetValue(payload, "delta"), out var delta))
                {
                    continue;
                }

                if (!contentBlocks.TryGetValue(index, out var block))
                {
                    block = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "text",
                        ["text"] = string.Empty
                    };
                    contentBlocks[index] = block;
                }

                var deltaType = StringValue(delta, "type", string.Empty);
                var blockType = StringValue(block, "type", string.Empty);
                if (deltaType == "thinking_delta"
                    || (blockType == "thinking" && deltaType == "text_delta"))
                {
                    var thinking = StringValue(delta, "thinking", string.Empty);
                    if (thinking.Length == 0)
                    {
                        thinking = StringValue(delta, "text", string.Empty);
                    }

                    if (thinking.Length == 0)
                    {
                        continue;
                    }

                    foreach (var line in EnsureRoleChunk())
                    {
                        yield return line;
                    }

                    yield return EmitChunk(new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["index"] = 0,
                            ["delta"] = new Dictionary<string, object?> { ["reasoning_content"] = thinking },
                            ["finish_reason"] = null
                        }
                    });
                }
                else if (deltaType == "text_delta")
                {
                    var text = StringValue(delta, "text", string.Empty);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    foreach (var line in EnsureRoleChunk())
                    {
                        yield return line;
                    }

                    yield return EmitChunk(new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["index"] = 0,
                            ["delta"] = new Dictionary<string, object?> { ["content"] = text },
                            ["finish_reason"] = null
                        }
                    });
                }
                else if (deltaType == "input_json_delta")
                {
                    var partialJson = StringValue(delta, "partial_json", string.Empty);
                    if (partialJson.Length == 0)
                    {
                        continue;
                    }

                    if (SkipToolNames?.Contains(StringValue(block, "name", string.Empty)) is true)
                    {
                        continue;
                    }

                    if (!chatToolIndexByBlock.TryGetValue(index, out var chatIndex))
                    {
                        chatToolIndexByBlock[index] = nextChatToolIndex++;
                        chatIndex = chatToolIndexByBlock[index];
                    }

                    foreach (var line in EnsureRoleChunk())
                    {
                        yield return line;
                    }

                    yield return EmitChunk(new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["index"] = 0,
                            ["delta"] = new Dictionary<string, object?>
                            {
                                ["tool_calls"] = new List<object?>
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["index"] = chatIndex,
                                        ["function"] = new Dictionary<string, object?>
                                        {
                                            ["arguments"] = partialJson
                                        }
                                    }
                                }
                            },
                            ["finish_reason"] = null
                        }
                    });
                }

                continue;
            }

            if (eventType == "content_block_stop")
            {
                // Chat 协议没有针对单个 block 的收尾事件；无需处理。
                continue;
            }

            if (eventType == "message_delta")
            {
                if (TryAsObject(GetValue(payload, "delta"), out var delta))
                {
                    var reason = StringValue(delta, "stop_reason", string.Empty);
                    if (reason.Length > 0)
                    {
                        stopReason = reason;
                    }
                }

                if (TryAsObject(GetValue(payload, "usage"), out var deltaUsage))
                {
                    foreach (var (key, value) in deltaUsage)
                    {
                        usage[key] = value;
                    }
                }

                continue;
            }

            if (eventType == "message_stop")
            {
                break;
            }
        }

        result.UpstreamResponse = upstreamResponseAccumulator.BuildResponse();

        // 即使没有内容也补一个 role chunk，保证 Chat 客户端能拿到 assistant 角色
        foreach (var line in EnsureRoleChunk())
        {
            yield return line;
        }

        var finishReason = MessagesStopReasonToChatFinishReason(stopReason);
        yield return EmitChunk(new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["index"] = 0,
                ["delta"] = new Dictionary<string, object?>(),
                ["finish_reason"] = finishReason
            }
        });

        // usage 单独的 chunk 仅在入口 Chat 请求 stream_options.include_usage=true 时输出。
        // 旧重载默认 true 以保持兼容，调用层可通过 IncludeUsage 精确控制。
        if (IncludeUsage)
        {
            var promptTokens = ToInt(GetValue(usage, "input_tokens"));
            var completionTokens = ToInt(GetValue(usage, "output_tokens"));
            var usageChunk = new Dictionary<string, object?>
            {
                ["id"] = completionId,
                ["object"] = "chat.completion.chunk",
                ["created"] = createdAt,
                ["model"] = responseModel,
                ["choices"] = new List<object?>(),
                ["usage"] = new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = promptTokens,
                    ["completion_tokens"] = completionTokens,
                    ["total_tokens"] = promptTokens + completionTokens
                }
            };
            yield return $"data: {JsonSerializer.Serialize(usageChunk, JsonOptions)}\n\n";
        }

        yield return "data: [DONE]\n\n";
    }

    private static string MessagesStopReasonToChatFinishReason(string stopReason)
    {
        return stopReason switch
        {
            "end_turn" => "stop",
            "max_tokens" => "length",
            "tool_use" => "tool_calls",
            "stop_sequence" => "stop",
            _ => "stop"
        };
    }
}

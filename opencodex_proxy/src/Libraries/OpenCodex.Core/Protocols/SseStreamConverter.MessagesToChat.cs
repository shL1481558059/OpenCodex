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
        var messageId = $"msg_{Guid.NewGuid():N}";
        var responseModel = model ?? string.Empty;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usage = new Dictionary<string, object?>(StringComparer.Ordinal);
        var stopReason = "end_turn";

        var textParts = new List<string>();
        var reasoningParts = new List<string>();
        var contentBlocks = new SortedDictionary<int, Dictionary<string, object?>>();
        var inputJsonParts = new Dictionary<int, List<string>>();

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
            if (!TryAsObject(sseEvent.Data, out var payload))
            {
                continue;
            }

            var eventType = StringValue(payload, "type", sseEvent.EventName);
            if (eventType == "error")
            {
                result.UpstreamResponse = payload;
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

                    if (blockType == "text")
                    {
                        var initialText = StringValue(block, "text", string.Empty);
                        if (initialText.Length > 0)
                        {
                            textParts.Add(initialText);
                        }
                    }
                    else if (blockType == "thinking")
                    {
                        var initialThinking = StringValue(block, "thinking", string.Empty);
                        if (initialThinking.Length > 0)
                        {
                            reasoningParts.Add(initialThinking);
                        }
                    }
                    else if (blockType == "tool_use")
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

                    reasoningParts.Add(thinking);
                    block["thinking"] = $"{StringValue(block, "thinking", string.Empty)}{thinking}";
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
                else if (deltaType == "signature_delta")
                {
                    // Chat 协议无签名概念，丢弃（不伪造），与非流式 ChatResponseToCanonical 行为一致。
                    var signature = StringValue(delta, "signature", string.Empty);
                    if (signature.Length > 0 && contentBlocks.TryGetValue(index, out var sigBlock))
                    {
                        sigBlock["signature"] = $"{StringValue(sigBlock, "signature", string.Empty)}{signature}";
                    }
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

                    textParts.Add(text);
                    block["text"] = $"{StringValue(block, "text", string.Empty)}{text}";
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
                    if (!inputJsonParts.TryGetValue(index, out var parts))
                    {
                        parts = [];
                        inputJsonParts[index] = parts;
                    }

                    var partialJson = StringValue(delta, "partial_json", string.Empty);
                    parts.Add(partialJson);

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

        // 工具 input 收尾：把 partial_json 拼装为对象，写入 contentBlocks 供 UpstreamResponse 使用
        foreach (var (index, parts) in inputJsonParts)
        {
            if (!contentBlocks.TryGetValue(index, out var block))
            {
                block = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "tool_use" };
                contentBlocks[index] = block;
            }

            block["input"] = ParseJsonObject(string.Concat(parts));
        }

        var orderedBlocks = contentBlocks.Values.Cast<object?>().ToList();
        // UpstreamResponse 取上游（Messages）格式，供 ProxyStreamService 的 ConvertResponse 转为 Chat 记录用
        result.UpstreamResponse = new Dictionary<string, object?>
        {
            ["id"] = messageId,
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = responseModel,
            ["content"] = orderedBlocks,
            ["stop_reason"] = stopReason,
            ["usage"] = usage
        };

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

using System.Text;
using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    // 上游协议: Responses (OpenAI /responses SSE，event: response.*)
    // 下游协议: Messages (Anthropic /v1/messages SSE)
    //
    // 输入: Responses 事件流
    // 输出: Anthropic Messages 事件流 message_start -> content_block_start/delta/stop -> message_delta -> message_stop
    //
    // 已知限制（与现有非流式 ConvertResponse 行为一致，不在流式侧隐藏）：
    //  - thinking 块无 signature / redacted_thinking：上游 Responses 的 reasoning 不携带 Anthropic 签名，
    //    多轮历史中 thinking 不可验证；不伪造签名。
    //  - custom_tool_call（apply_patch 等）的 input 不逐字实时流出：上游给的是解码后的 patch 文本，
    //    而 Anthropic input_json_delta 必须是 JSON 片段；在 output_item.done 时一次性把完整 input
    //    序列化为 JSON 作为 input_json_delta 发出。function_call 的 arguments 是 JSON 片段，可安全逐字实时流出。
    //  - 若检测到 tool_use 输出且上游 status == completed，stop_reason 设为 tool_use（符合 Anthropic 协议，
    //    便于客户端触发工具循环）；其余按 status 映射 completed->end_turn、incomplete->max_tokens。
    public static async IAsyncEnumerable<string> ResponsesToMessagesEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ResponsesToMessagesEvents(
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

    public static async IAsyncEnumerable<string> ResponsesToMessagesEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        IReadOnlySet<string>? SkipToolNames,
        bool SkipMessageStart,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messageId = $"msg_{Guid.NewGuid():N}";
        var responseModel = model ?? string.Empty;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var responseId = $"resp_{Guid.NewGuid():N}";
        var finishStatus = "completed";
        var usage = new Dictionary<string, object?>(StringComparer.Ordinal);

        var textParts = new List<string>();
        var refusalParts = new List<string>();
        var reasoningParts = new List<string>();
        var annotations = new List<object?>();
        var nextBlockIndex = 0;
        var openBlockIndex = (int?)null;
        var thinkingStarted = false;
        var thinkingIndex = (int?)null;
        var textStarted = false;
        var textIndex = (int?)null;

        var toolStates = new Dictionary<int, ResponsesToMessagesToolState>();
        var outputByIndex = new SortedDictionary<int, Dictionary<string, object?>>();
        var pendingToolUseCount = 0;
        var responseAccumulator = new ResponsesStreamResponseAccumulator(
            new StreamCaptureBudget(int.MaxValue, int.MaxValue));

        string Emit(string eventName, Dictionary<string, object?> payload)
        {
            payload["type"] = eventName;
            return $"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, JsonOptions)}\n\n";
        }

        int AllocateBlockIndex() => nextBlockIndex++;

        void CloseOpenBlock(List<string> output)
        {
            if (openBlockIndex is null)
            {
                return;
            }

            output.Add(Emit("content_block_stop", new Dictionary<string, object?>
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
            output.Add(Emit("content_block_start", new Dictionary<string, object?>
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
            output.Add(Emit("content_block_start", new Dictionary<string, object?>
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

        var enumerator = ParseEvents(upstreamLines, cancellationToken).GetAsyncEnumerator(cancellationToken);

        if (!SkipMessageStart)
        {
            yield return Emit("message_start", new Dictionary<string, object?>
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
            responseAccumulator.Accept(sseEvent);
            if (!TryAsObject(sseEvent.Data, out var payload))
            {
                continue;
            }

            var eventType = StringValue(payload, "type", sseEvent.EventName);
            if (eventType == "response.created" || eventType == "response.in_progress")
            {
                if (TryAsObject(GetValue(payload, "response"), out var response))
                {
                    responseId = StringValue(response, "id", responseId);
                    responseModel = model ?? StringValue(response, "model", responseModel);
                    if (TryAsObject(GetValue(response, "usage"), out var responseUsage))
                    {
                        usage = responseUsage;
                    }
                }
                continue;
            }

            if (eventType == "response.output_text.delta")
            {
                var text = StringValue(payload, "delta", string.Empty);
                if (text.Length == 0)
                {
                    continue;
                }

                foreach (var line in EnsureTextStarted())
                {
                    yield return line;
                }

                textParts.Add(text);
                yield return Emit("content_block_delta", new Dictionary<string, object?>
                {
                    ["index"] = textIndex,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["type"] = "text_delta",
                        ["text"] = text
                    }
                });
                continue;
            }

            if (eventType == "response.refusal.delta" || eventType == "response.refusal.done")
            {
                var completeRefusal = eventType == "response.refusal.done"
                    ? StringValue(payload, "refusal", string.Empty)
                    : string.Empty;
                var refusal = eventType == "response.refusal.delta"
                    ? StringValue(payload, "delta", string.Empty)
                    : MissingSuffix(string.Concat(refusalParts), completeRefusal);
                if (refusal.Length == 0)
                {
                    continue;
                }

                foreach (var line in EnsureTextStarted())
                {
                    yield return line;
                }

                refusalParts.Add(refusal);
                yield return Emit("content_block_delta", new Dictionary<string, object?>
                {
                    ["index"] = textIndex,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["type"] = "text_delta",
                        ["text"] = refusal
                    }
                });
                continue;
            }

            if (eventType == "response.output_text.annotation.added")
            {
                if (!TryAsObject(GetValue(payload, "annotation"), out var annotation))
                {
                    continue;
                }

                annotations.Add(annotation);
                if (!TryConvertResponsesAnnotationToAnthropic(annotation, out var citation))
                {
                    continue;
                }

                foreach (var line in EnsureTextStarted())
                {
                    yield return line;
                }

                yield return Emit("content_block_delta", new Dictionary<string, object?>
                {
                    ["index"] = textIndex,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["type"] = "citations_delta",
                        ["citation"] = citation
                    }
                });
                continue;
            }

            if (eventType == "response.reasoning_summary_text.delta")
            {
                var text = StringValue(payload, "delta", string.Empty);
                if (text.Length == 0)
                {
                    continue;
                }

                foreach (var line in EnsureThinkingStarted())
                {
                    yield return line;
                }

                reasoningParts.Add(text);
                yield return Emit("content_block_delta", new Dictionary<string, object?>
                {
                    ["index"] = thinkingIndex,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["type"] = "thinking_delta",
                        ["thinking"] = text
                    }
                });
                continue;
            }

            if (eventType == "response.output_item.added")
            {
                var outputIndex = ToInt(GetValue(payload, "output_index"));
                if (!TryAsObject(GetValue(payload, "item"), out var item))
                {
                    continue;
                }

                var itemType = StringValue(item, "type", string.Empty);
                if (itemType is "mcp_list_tools" or "mcp_approval_request" or "mcp_approval_response")
                {
                    throw new InvalidOperationException(
                        $"Responses stream item '{itemType}' has no compatible Anthropic Messages streaming representation.");
                }

                var isMcpCall = itemType == "mcp_call";
                if (IsServerExecutedNativeToolCallType(itemType)
                    || IsServerExecutedToolSearchCall(item))
                {
                    outputByIndex[outputIndex] = new Dictionary<string, object?>(item, StringComparer.Ordinal);
                    continue;
                }

                if (!isMcpCall
                    && !TryGetClientToolCallInfo(item, out _, out _, out _))
                {
                    if (IsResponsesNativeToolCallType(itemType))
                    {
                        throw new InvalidOperationException(
                            $"Responses native stream item '{itemType}' has no compatible Anthropic Messages representation.");
                    }

                    continue;
                }

                var toolName = StringValue(item, "name", string.Empty);
                if (string.IsNullOrEmpty(toolName) || SkipToolNames?.Contains(toolName) is true)
                {
                    continue;
                }

                var callId = StringValue(item, "call_id", string.Empty);
                if (string.IsNullOrEmpty(callId))
                {
                    callId = StringValue(item, "id", $"call_{Guid.NewGuid():N}");
                }

                var blockIndex = AllocateBlockIndex();
                toolStates[outputIndex] = new ResponsesToMessagesToolState
                {
                    BlockIndex = blockIndex,
                    CallId = callId,
                    Name = toolName,
                    CallKind = isMcpCall
                        ? ResponsesToolCallKind.NativeTool
                        : ProtocolConverter.GetResponsesToolCallKind(toolName),
                    IsMcp = isMcpCall
                };
                pendingToolUseCount++;

                var startOutput = new List<string>();
                CloseOpenBlock(startOutput);
                foreach (var line in startOutput)
                {
                    yield return line;
                }

                openBlockIndex = blockIndex;
                var contentBlock = new Dictionary<string, object?>
                {
                    ["type"] = isMcpCall ? "mcp_tool_use" : "tool_use",
                    ["id"] = callId,
                    ["name"] = toolName,
                    ["input"] = new Dictionary<string, object?>()
                };
                if (isMcpCall)
                {
                    contentBlock["server_name"] = StringValue(
                        item,
                        "server_label",
                        StringValue(item, "server_name", string.Empty));
                }

                yield return Emit("content_block_start", new Dictionary<string, object?>
                {
                    ["index"] = blockIndex,
                    ["content_block"] = contentBlock
                });
                continue;
            }

            if (eventType == "response.function_call_arguments.delta"
                || eventType.EndsWith("_call.arguments.delta", StringComparison.Ordinal)
                || eventType.EndsWith("_call_arguments.delta", StringComparison.Ordinal))
            {
                var outputIndex = ToInt(GetValue(payload, "output_index"));
                if (!toolStates.TryGetValue(outputIndex, out var state)
                    || state.CallKind == ResponsesToolCallKind.CustomTool)
                {
                    continue;
                }

                var deltaText = StringValue(payload, "delta", string.Empty);
                if (deltaText.Length == 0)
                {
                    continue;
                }

                yield return Emit("content_block_delta", new Dictionary<string, object?>
                {
                    ["index"] = state.BlockIndex,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["type"] = "input_json_delta",
                        ["partial_json"] = deltaText
                    }
                });
                state.ArgumentsDeltaEmitted = true;
                continue;
            }

            if (eventType == "response.custom_tool_call_input.delta")
            {
                var outputIndex = ToInt(GetValue(payload, "output_index"));
                if (!toolStates.TryGetValue(outputIndex, out var state)
                    || state.CallKind != ResponsesToolCallKind.CustomTool)
                {
                    continue;
                }

                var deltaText = StringValue(payload, "delta", string.Empty);
                state.InputBuilder ??= new StringBuilder();
                state.InputBuilder.Append(deltaText);
                continue;
            }

            if (eventType == "response.output_item.done")
            {
                var outputIndex = ToInt(GetValue(payload, "output_index"));
                if (!TryAsObject(GetValue(payload, "item"), out var item))
                {
                    continue;
                }

                var completedItemType = StringValue(item, "type", string.Empty);
                if (!toolStates.ContainsKey(outputIndex)
                    && (IsNativeMcpItemType(completedItemType)
                        || (IsResponsesNativeToolCallType(completedItemType)
                            && !IsServerExecutedNativeToolCallType(completedItemType)
                            && !IsServerExecutedToolSearchCall(item))))
                {
                    throw new InvalidOperationException(
                        $"Responses stream item '{completedItemType}' completed without a compatible Anthropic content-block start event.");
                }

                outputByIndex[outputIndex] = new Dictionary<string, object?>(item, StringComparer.Ordinal);

                // custom_tool_call：done 时把完整 input 序列化为 JSON 作为 input_json_delta 一次性发出
                if (toolStates.TryGetValue(outputIndex, out var state)
                    && state.CallKind == ResponsesToolCallKind.CustomTool)
                {
                    var input = GetValue(item, "input") ?? new Dictionary<string, object?>();
                    var normalizedInput = ProtocolConverter.IsApplyPatchPublic(state.Name) && input is string patch
                        ? new Dictionary<string, object?> { ["patch"] = patch }
                        : input;
                    var argumentsJson = JsonSerializer.Serialize(NormalizeJsonValueForMessages(normalizedInput), JsonOptions);
                    yield return Emit("content_block_delta", new Dictionary<string, object?>
                    {
                        ["index"] = state.BlockIndex,
                        ["delta"] = new Dictionary<string, object?>
                        {
                            ["type"] = "input_json_delta",
                            ["partial_json"] = argumentsJson
                        }
                    });
                }
                else if (toolStates.TryGetValue(outputIndex, out state)
                    && !state.ArgumentsDeltaEmitted
                    && TryGetToolArguments(item, out var argumentsJson))
                {
                    yield return Emit("content_block_delta", new Dictionary<string, object?>
                    {
                        ["index"] = state.BlockIndex,
                        ["delta"] = new Dictionary<string, object?>
                        {
                            ["type"] = "input_json_delta",
                            ["partial_json"] = argumentsJson
                        }
                    });
                }

                if (toolStates.TryGetValue(outputIndex, out state) && state.IsMcp)
                {
                    var mcpOutput = GetValue(item, "output");
                    var mcpError = GetValue(item, "error");
                    if (mcpOutput is not null || mcpError is not null)
                    {
                        pendingToolUseCount = Math.Max(0, pendingToolUseCount - 1);
                        var resultOutput = new List<string>();
                        CloseOpenBlock(resultOutput);
                        foreach (var line in resultOutput)
                        {
                            yield return line;
                        }

                        var resultIndex = AllocateBlockIndex();
                        yield return Emit("content_block_start", new Dictionary<string, object?>
                        {
                            ["index"] = resultIndex,
                            ["content_block"] = new Dictionary<string, object?>
                            {
                                ["type"] = "mcp_tool_result",
                                ["tool_use_id"] = state.CallId,
                                ["is_error"] = mcpError is not null,
                                ["content"] = BuildAnthropicMcpResultContent(mcpError ?? mcpOutput)
                            }
                        });
                        yield return Emit("content_block_stop", new Dictionary<string, object?>
                        {
                            ["index"] = resultIndex
                        });
                    }
                }

                continue;
            }

            if (eventType == "response.completed" || eventType == "response.incomplete")
            {
                if (TryAsObject(GetValue(payload, "response"), out var response))
                {
                    responseId = StringValue(response, "id", responseId);
                    responseModel = model ?? StringValue(response, "model", responseModel);
                    finishStatus = StringValue(
                        response,
                        "status",
                        eventType == "response.incomplete" ? "incomplete" : finishStatus);
                    if (TryAsObject(GetValue(response, "usage"), out var responseUsage))
                    {
                        usage = responseUsage;
                    }

                    if (TryAsList(GetValue(response, "output"), out var responseOutput))
                    {
                        outputByIndex.Clear();
                        for (var i = 0; i < responseOutput.Count; i++)
                        {
                            if (TryAsObject(responseOutput[i], out var doneItem))
                            {
                                var doneItemType = StringValue(doneItem, "type", string.Empty);
                                if ((IsNativeMcpItemType(doneItemType)
                                        || (IsResponsesNativeToolCallType(doneItemType)
                                            && !IsServerExecutedNativeToolCallType(doneItemType)
                                            && !IsServerExecutedToolSearchCall(doneItem)))
                                    && !toolStates.ContainsKey(i))
                                {
                                    throw new InvalidOperationException(
                                        $"Responses terminal output item '{doneItemType}' has no compatible Anthropic stream representation.");
                                }

                                outputByIndex[i] = new Dictionary<string, object?>(doneItem, StringComparer.Ordinal);
                            }
                        }
                    }
                }
                break;
            }

            if (eventType == "response.failed")
            {
                var capturedFailure = responseAccumulator.BuildResponse()
                    ?? new Dictionary<string, object?>(StringComparer.Ordinal);
                responseId = StringValue(capturedFailure, "id", responseId);
                responseModel = model ?? StringValue(capturedFailure, "model", responseModel);
                var failedResponse = BuildCapturedResponsesUpstreamResponse(
                    responseAccumulator,
                    "response.failed",
                    responseId,
                    createdAt,
                    "failed",
                    responseModel,
                    outputByIndex.Values,
                    usage,
                    preserveCapturedOutputAndUsage: true)!;
                result.UpstreamResponse = failedResponse;

                var closing = new List<string>();
                CloseOpenBlock(closing);
                foreach (var line in closing)
                {
                    yield return line;
                }

                var upstreamError = GetValue(failedResponse, "error");
                var error = TryAsObject(upstreamError, out var errorObject)
                    ? new Dictionary<string, object?>(errorObject, StringComparer.Ordinal)
                    : new Dictionary<string, object?>
                    {
                        ["type"] = "api_error",
                        ["message"] = upstreamError?.ToString() ?? "The upstream Responses stream failed."
                    };
                error.TryAdd("type", "api_error");
                error.TryAdd("message", "The upstream Responses stream failed.");
                yield return Emit("error", new Dictionary<string, object?> { ["error"] = error });
                yield break;
            }
        }

        var combinedText = string.Concat(textParts);
        var combinedReasoning = string.Concat(reasoningParts);

        // 确保 output 含 message / reasoning 项，供 ProxyStreamService 的 ConvertResponse 提取
        if ((combinedText.Length > 0 || refusalParts.Count > 0)
            && !outputByIndex.Values.Any(o => StringValue(o, "type", string.Empty) == "message"))
        {
            var msgIndex = outputByIndex.Count > 0 ? outputByIndex.Keys.Max() + 1 : 0;
            outputByIndex[msgIndex] = new Dictionary<string, object?>
            {
                ["id"] = $"msg_{Guid.NewGuid():N}",
                ["type"] = "message",
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = BuildResponsesMessageContent(combinedText, refusalParts, annotations)
            };
        }

        if (combinedReasoning.Length > 0 && !outputByIndex.Values.Any(o => StringValue(o, "type", string.Empty) == "reasoning"))
        {
            var rsIndex = outputByIndex.Count > 0 ? outputByIndex.Keys.Max() + 1 : 0;
            outputByIndex[rsIndex] = new Dictionary<string, object?>
            {
                ["id"] = $"rs_{Guid.NewGuid():N}",
                ["type"] = "reasoning",
                ["status"] = "completed",
                ["summary"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "summary_text",
                        ["text"] = combinedReasoning
                    }
                },
                ["encrypted_content"] = combinedReasoning
            };
        }

        result.UpstreamResponse = BuildCapturedResponsesUpstreamResponse(
            responseAccumulator,
            finishStatus == "incomplete" ? "response.incomplete" : "response.completed",
            responseId,
            createdAt,
            finishStatus,
            responseModel,
            outputByIndex.Values,
            usage);

        // 关闭最后一个仍打开的块
        var closingOutput = new List<string>();
        CloseOpenBlock(closingOutput);
        foreach (var line in closingOutput)
        {
            yield return line;
        }

        var stopReason = ResponsesStatusToMessagesStopReason(finishStatus, pendingToolUseCount > 0);
        var outputTokens = ToInt(GetValue(usage, "output_tokens"));
        yield return Emit("message_delta", new Dictionary<string, object?>
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

        yield return Emit("message_stop", new Dictionary<string, object?>());
    }

    private static string ResponsesStatusToMessagesStopReason(string status, bool hasToolUse)
    {
        if (status == "incomplete")
        {
            return "max_tokens";
        }

        if (hasToolUse)
        {
            return "tool_use";
        }

        return "end_turn";
    }

    private static object? NormalizeJsonValueForMessages(object? value)
    {
        if (value is Dictionary<string, object?> dict)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, val) in dict)
            {
                result[key] = NormalizeJsonValueForMessages(val);
            }
            return result;
        }

        if (value is List<object?> list)
        {
            return list.Select(NormalizeJsonValueForMessages).ToList();
        }

        return value;
    }

    private static bool TryConvertResponsesAnnotationToAnthropic(
        Dictionary<string, object?> annotation,
        out Dictionary<string, object?> citation)
    {
        citation = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (StringValue(annotation, "type", string.Empty) != "url_citation")
        {
            return false;
        }

        citation["type"] = "web_search_result_location";
        citation["cited_text"] = StringValue(annotation, "cited_text", StringValue(annotation, "text", string.Empty));
        citation["encrypted_index"] = StringValue(annotation, "encrypted_index", string.Empty);
        citation["title"] = StringValue(annotation, "title", string.Empty);
        citation["url"] = StringValue(annotation, "url", string.Empty);
        return citation["url"]?.ToString()?.Length > 0;
    }

    private static List<object?> BuildAnthropicMcpResultContent(object? value)
    {
        if (value is List<object?> list)
        {
            return list;
        }

        var text = value is string stringValue
            ? stringValue
            : JsonSerializer.Serialize(NormalizeJsonValueForMessages(value), JsonOptions);
        return new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = text
            }
        };
    }
}

internal sealed class ResponsesToMessagesToolState
{
    public int BlockIndex { get; set; }
    public string CallId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ResponsesToolCallKind CallKind { get; set; } = ResponsesToolCallKind.Function;
    public StringBuilder? InputBuilder { get; set; }
    public bool ArgumentsDeltaEmitted { get; set; }
    public bool IsMcp { get; set; }
}

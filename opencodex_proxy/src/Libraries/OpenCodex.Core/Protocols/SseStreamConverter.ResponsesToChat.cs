using System.Text;
using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    // 上游协议: Responses (OpenAI /responses SSE，event: response.*)
    // 下游协议: Chat (OpenAI /v1/chat/completions SSE)
    //
    // 输入: Responses 事件流 response.created/in_progress/output_item.added/done +
    //       output_text.delta/reasoning_summary_text.delta/function_call_arguments.delta/custom_tool_call_input.delta +
    //       response.completed
    // 输出: OpenAI chat.completion.chunk 流，末尾 data: [DONE]
    //
    // 已知限制（与现有非流式 ConvertResponse 行为一致，不在流式侧隐藏）：
    //  - reasoning 的 encrypted_content 不会被还原为签名：Chat 协议的 reasoning_content 仅是纯文本反馈，无签名机制。
    //  - custom_tool_call（apply_patch 等）的 input 不逐字实时流出：上游 Responses 给的是解码后的 patch 文本，
    //    而 Chat function.arguments 必须是 JSON 字符串；为保证 arguments 仍是合法 JSON，
    //    在 output_item.done 时一次性把完整 input 序列化为 JSON 作为 arguments 发出。
    //    function_call 的 arguments 是 JSON 片段，可安全逐字实时流出。
    public static async IAsyncEnumerable<string> ResponsesToChatEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ResponsesToChatEvents(
            upstreamLines,
            model,
            result,
            SkipToolNames: null,
            cancellationToken))
        {
            yield return line;
        }
    }

    public static async IAsyncEnumerable<string> ResponsesToChatEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        IReadOnlySet<string>? SkipToolNames,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ResponsesToChatEvents(ParseEvents(upstreamLines, cancellationToken), model, result, SkipToolNames, cancellationToken)) yield return line;
    }

    internal static async IAsyncEnumerable<string> ResponsesToChatEvents(
        IAsyncEnumerable<SseEvent> upstreamEvents,
        string? model,
        ConvertedStreamResult result,
        IReadOnlySet<string>? SkipToolNames,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in ResponsesToChatFrames(upstreamEvents, model, result, SkipToolNames, cancellationToken)) yield return frame.WireText;
    }

    internal static async IAsyncEnumerable<SseFrame> ResponsesToChatFrames(
        IAsyncEnumerable<SseEvent> upstreamEvents,
        string? model,
        ConvertedStreamResult result,
        IReadOnlySet<string>? SkipToolNames,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completionId = $"chatcmpl_{Guid.NewGuid():N}";
        var streamState = new ResponsesStreamState(model, result);
        var firstRoleEmitted = false;
        var nextChatToolIndex = 0;
        var toolStates = new Dictionary<int, ChatToolStreamState>();

        SseFrame EmitChunk(List<object?> choices)
        {
            var chunk = new Dictionary<string, object?>
            {
                ["id"] = completionId,
                ["object"] = "chat.completion.chunk",
                ["created"] = streamState.CreatedAt,
                ["model"] = streamState.ResponseModel,
                ["choices"] = choices
            };
            return SseFrame.FromJson(chunk);
        }

        List<SseFrame> EnsureRoleChunk()
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

        await using var enumerator = upstreamEvents.GetAsyncEnumerator(cancellationToken);

        while (await enumerator.MoveNextAsync())
        {
            var sseEvent = enumerator.Current;
            streamState.Accept(sseEvent);
            if (!ResponsesStreamEvent.TryCreate(sseEvent, out var streamEvent))
            {
                continue;
            }

            var payload = streamEvent.Payload;
            if (streamEvent.Kind is ResponsesStreamEventKind.Created or ResponsesStreamEventKind.InProgress)
            {
                if (streamEvent.TryGetResponse(out var response))
                {
                    streamState.ApplyResponse(response);
                }
                foreach(var frame in EnsureRoleChunk())yield return frame;
                continue;
            }

            if (streamEvent.Kind == ResponsesStreamEventKind.OutputItemAdded)
            {
                var outputIndex = streamEvent.OutputIndex;
                if (!streamEvent.TryGetItem(out var item))
                {
                    continue;
                }

                var itemType = StringValue(item, "type", string.Empty);
                if (IsNativeMcpItemType(itemType))
                {
                    throw new InvalidOperationException(
                        $"Responses stream item '{itemType}' cannot be represented by Chat Completions without changing MCP execution semantics.");
                }

                if (IsServerExecutedNativeToolCallType(itemType)
                    || IsServerExecutedToolSearchCall(item))
                {
                    streamState.OutputByIndex[outputIndex] = new Dictionary<string, object?>(item, StringComparer.Ordinal);
                    continue;
                }

                if (!TryGetClientToolCallInfo(item, out var toolName, out var callKind, out _))
                {
                    if (IsResponsesNativeToolCallType(itemType))
                    {
                        throw new InvalidOperationException(
                            $"Responses native stream item '{itemType}' has no compatible Chat Completions representation.");
                    }

                    continue;
                }

                if (SkipToolNames?.Contains(toolName) is true)
                {
                    continue;
                }

                var callId = StringValue(item, "call_id", string.Empty);
                if (string.IsNullOrEmpty(callId))
                {
                    callId = StringValue(item, "id", $"call_{Guid.NewGuid():N}");
                }

                var chatIndex = nextChatToolIndex++;
                toolStates[outputIndex] = new ChatToolStreamState
                {
                    ChatIndex = chatIndex,
                    CallId = callId,
                    Name = toolName,
                    CallKind = callKind
                };

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
                                    ["id"] = callId,
                                    ["type"] = "function",
                                    ["function"] = new Dictionary<string, object?>
                                    {
                                        ["name"] = toolName,
                                        ["arguments"] = string.Empty
                                    }
                                }
                            }
                        },
                        ["finish_reason"] = null
                    }
                });
                continue;
            }

            if (streamEvent.Kind == ResponsesStreamEventKind.OutputTextDelta)
            {
                var text = streamEvent.Delta;
                if (text.Length == 0)
                {
                    continue;
                }

                foreach (var line in EnsureRoleChunk())
                {
                    yield return line;
                }

                streamState.TextParts.Add(text,result.SharedCapture?.BorrowText(payload,"delta"));
                yield return EmitChunk(new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["delta"] = new Dictionary<string, object?> { ["content"] = text },
                        ["finish_reason"] = null
                    }
                });
                continue;
            }

            if (streamEvent.Kind is ResponsesStreamEventKind.RefusalDelta or ResponsesStreamEventKind.RefusalDone)
            {
                var completeRefusal = streamEvent.Kind == ResponsesStreamEventKind.RefusalDone
                    ? streamEvent.Refusal
                    : string.Empty;
                var refusal = streamEvent.Kind == ResponsesStreamEventKind.RefusalDelta
                    ? streamEvent.Delta
                    : MissingSuffix(string.Concat(streamState.RefusalParts), completeRefusal);
                if (refusal.Length == 0)
                {
                    continue;
                }

                foreach (var line in EnsureRoleChunk())
                {
                    yield return line;
                }

                streamState.RefusalParts.Add(refusal,result.SharedCapture?.BorrowText(payload,"delta"));
                yield return EmitChunk(new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["delta"] = new Dictionary<string, object?> { ["refusal"] = refusal },
                        ["finish_reason"] = null
                    }
                });
                continue;
            }

            if (streamEvent.Kind == ResponsesStreamEventKind.AnnotationAdded)
            {
                if (streamEvent.Annotation is not { } annotation)
                {
                    continue;
                }

                foreach (var line in EnsureRoleChunk())
                {
                    yield return line;
                }

                streamState.Annotations.Add(annotation);
                yield return EmitChunk(new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["delta"] = new Dictionary<string, object?>
                        {
                            ["annotations"] = new List<object?> { annotation }
                        },
                        ["finish_reason"] = null
                    }
                });
                continue;
            }

            if (streamEvent.Kind == ResponsesStreamEventKind.ReasoningDelta)
            {
                var text = streamEvent.Delta;
                if (text.Length == 0)
                {
                    continue;
                }

                foreach (var line in EnsureRoleChunk())
                {
                    yield return line;
                }

                streamState.ReasoningParts.Add(text,result.SharedCapture?.BorrowText(payload,"delta"));
                yield return EmitChunk(new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["delta"] = new Dictionary<string, object?> { ["reasoning_content"] = text },
                        ["finish_reason"] = null
                    }
                });
                continue;
            }

            if (streamEvent.Kind == ResponsesStreamEventKind.ToolArgumentsDelta)
            {
                var outputIndex = streamEvent.OutputIndex;
                if (!toolStates.TryGetValue(outputIndex, out var state)
                    || state.CallKind == ResponsesToolCallKind.CustomTool)
                {
                    continue;
                }

                var deltaText = streamEvent.Delta;
                if (deltaText.Length == 0)
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
                        ["delta"] = new Dictionary<string, object?>
                        {
                            ["tool_calls"] = new List<object?>
                            {
                                new Dictionary<string, object?>
                                {
                                    ["index"] = state.ChatIndex,
                                    ["function"] = new Dictionary<string, object?>
                                    {
                                        ["arguments"] = deltaText
                                    }
                                }
                            }
                        },
                        ["finish_reason"] = null
                    }
                });
                state.ArgumentsDeltaEmitted = true;
                continue;
            }

            if (streamEvent.Kind == ResponsesStreamEventKind.CustomToolInputDelta)
            {
                // custom tool 的 input 不实时流（见文件头注释），仅累积
                var outputIndex = streamEvent.OutputIndex;
                if (!toolStates.TryGetValue(outputIndex, out var state)
                    || state.CallKind != ResponsesToolCallKind.CustomTool)
                {
                    continue;
                }

                var deltaText = StringValue(payload, "delta", string.Empty);
                continue;
            }

            if (streamEvent.Kind == ResponsesStreamEventKind.OutputItemDone)
            {
                var outputIndex = streamEvent.OutputIndex;
                if (!streamEvent.TryGetItem(out var item))
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
                        $"Responses stream item '{completedItemType}' completed without a compatible Chat tool-call start event.");
                }

                streamState.OutputByIndex[outputIndex] = new Dictionary<string, object?>(item, StringComparer.Ordinal);

                // custom_tool_call：done 时把完整 input 序列化为 JSON 作为 arguments 一次性发出
                if (toolStates.TryGetValue(outputIndex, out var state)
                    && state.CallKind == ResponsesToolCallKind.CustomTool)
                {
                    var input = GetValue(item, "input") ?? new Dictionary<string, object?>();
                    var normalizedInput = ProtocolConverter.IsApplyPatchPublic(state.Name) && input is string patch
                        ? new Dictionary<string, object?> { ["patch"] = patch }
                        : input;
                    var argumentsJson = JsonSerializer.Serialize(NormalizeResponsesStreamJsonValue(normalizedInput), JsonOptions);
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
                                        ["index"] = state.ChatIndex,
                                        ["function"] = new Dictionary<string, object?>
                                        {
                                            ["arguments"] = argumentsJson
                                        }
                                    }
                                }
                            },
                            ["finish_reason"] = null
                        }
                    });
                }
                else if (toolStates.TryGetValue(outputIndex, out state)
                    && !state.ArgumentsDeltaEmitted
                    && TryGetToolArguments(item, out var argumentsJson))
                {
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
                                        ["index"] = state.ChatIndex,
                                        ["function"] = new Dictionary<string, object?>
                                        {
                                            ["arguments"] = argumentsJson
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

            if (streamEvent.Kind is ResponsesStreamEventKind.Completed or ResponsesStreamEventKind.Incomplete)
            {
                streamState.ApplyTerminalResponse(
                    payload,
                    streamEvent.Type,
                    toolStates.ContainsKey,
                    "Chat stream");
                break;
            }

            if (streamEvent.Kind == ResponsesStreamEventKind.Failed)
            {
                var capturedFailure = streamState.Accumulator.BuildResponse()
                    ?? new Dictionary<string, object?>(StringComparer.Ordinal);
                streamState.ApplyCapturedResponse(capturedFailure);
                var failedResponse = streamState.BuildCapturedResponse(
                    "response.failed",
                    "failed",
                    preserveCapturedOutputAndUsage: true);
                result.UpstreamResponse = failedResponse;

                var error = GetValue(failedResponse, "error") ?? new Dictionary<string, object?>
                {
                    ["code"] = "response_failed",
                    ["message"] = "The upstream Responses stream failed."
                };
                yield return SseFrame.FromJson(new Dictionary<string, object?> { ["error"] = error });
                yield break;
            }
        }

        streamState.AddFallbackOutputItems();
        result.UpstreamResponse = streamState.BuildCapturedResponse(
            streamState.FinishStatus == "incomplete" ? "response.incomplete" : "response.completed",
            streamState.FinishStatus);

        foreach (var line in EnsureRoleChunk())
        {
            yield return line;
        }

        var finishReason = streamState.FinishStatus == "incomplete"
            ? "length"
            : toolStates.Count > 0 ? "tool_calls" : "stop";
        yield return EmitChunk(new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["index"] = 0,
                ["delta"] = new Dictionary<string, object?>(),
                ["finish_reason"] = finishReason
            }
        });

        var promptTokens = ToInt(GetValue(streamState.Usage, "input_tokens"));
        var completionTokens = ToInt(GetValue(streamState.Usage, "output_tokens"));
        if (promptTokens > 0 || completionTokens > 0)
        {
            var usageChunk = new Dictionary<string, object?>
            {
                ["id"] = completionId,
                ["object"] = "chat.completion.chunk",
                ["created"] = streamState.CreatedAt,
                ["model"] = streamState.ResponseModel,
                ["choices"] = new List<object?>(),
                ["usage"] = new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = promptTokens,
                    ["completion_tokens"] = completionTokens,
                    ["total_tokens"] = promptTokens + completionTokens
                }
            };
            yield return SseFrame.FromJson(usageChunk);
        }

        yield return SseFrame.Done;
    }

}

internal sealed class ChatToolStreamState
{
    public int ChatIndex { get; set; }
    public string CallId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ResponsesToolCallKind CallKind { get; set; } = ResponsesToolCallKind.Function;
    public bool ArgumentsDeltaEmitted { get; set; }
}

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
        var completionId = $"chatcmpl_{Guid.NewGuid():N}";
        var responseModel = model ?? string.Empty;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var responseId = $"resp_{Guid.NewGuid():N}";
        var finishStatus = "completed";
        var usage = new Dictionary<string, object?>(StringComparer.Ordinal);

        var textParts = new List<string>();
        var refusalParts = new List<string>();
        var reasoningParts = new List<string>();
        var annotations = new List<object?>();
        var firstRoleEmitted = false;
        var nextChatToolIndex = 0;
        var toolStates = new Dictionary<int, ChatToolStreamState>();
        var outputByIndex = new SortedDictionary<int, Dictionary<string, object?>>();

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

        var enumerator = ParseEvents(upstreamLines, cancellationToken).GetAsyncEnumerator(cancellationToken);

        while (await enumerator.MoveNextAsync())
        {
            var sseEvent = enumerator.Current;
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

            if (eventType == "response.output_item.added")
            {
                var outputIndex = ToInt(GetValue(payload, "output_index"));
                if (!TryAsObject(GetValue(payload, "item"), out var item))
                {
                    continue;
                }

                var itemType = StringValue(item, "type", string.Empty);
                if (IsNativeMcpItemType(itemType))
                {
                    throw new InvalidOperationException(
                        $"Responses stream item '{itemType}' cannot be represented by Chat Completions without changing MCP execution semantics.");
                }

                if (IsServerExecutedNativeToolCallType(itemType))
                {
                    outputByIndex[outputIndex] = new Dictionary<string, object?>(item, StringComparer.Ordinal);
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

            if (eventType == "response.output_text.delta")
            {
                var text = StringValue(payload, "delta", string.Empty);
                if (text.Length == 0)
                {
                    continue;
                }

                foreach (var line in EnsureRoleChunk())
                {
                    yield return line;
                }

                textParts.Add(text);
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

                foreach (var line in EnsureRoleChunk())
                {
                    yield return line;
                }

                refusalParts.Add(refusal);
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

            if (eventType == "response.output_text.annotation.added")
            {
                if (GetValue(payload, "annotation") is not { } annotation)
                {
                    continue;
                }

                foreach (var line in EnsureRoleChunk())
                {
                    yield return line;
                }

                annotations.Add(annotation);
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

            if (eventType == "response.reasoning_summary_text.delta")
            {
                var text = StringValue(payload, "delta", string.Empty);
                if (text.Length == 0)
                {
                    continue;
                }

                foreach (var line in EnsureRoleChunk())
                {
                    yield return line;
                }

                reasoningParts.Add(text);
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

            if (eventType == "response.custom_tool_call_input.delta")
            {
                // custom tool 的 input 不实时流（见文件头注释），仅累积
                var outputIndex = ToInt(GetValue(payload, "output_index"));
                if (!toolStates.TryGetValue(outputIndex, out var state)
                    || state.CallKind != ResponsesToolCallKind.CustomTool)
                {
                    continue;
                }

                var deltaText = StringValue(payload, "delta", string.Empty);
                state.CustomInputBuilder ??= new StringBuilder();
                state.CustomInputBuilder.Append(deltaText);
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
                            && !IsServerExecutedNativeToolCallType(completedItemType))))
                {
                    throw new InvalidOperationException(
                        $"Responses stream item '{completedItemType}' completed without a compatible Chat tool-call start event.");
                }

                outputByIndex[outputIndex] = new Dictionary<string, object?>(item, StringComparer.Ordinal);

                // custom_tool_call：done 时把完整 input 序列化为 JSON 作为 arguments 一次性发出
                if (toolStates.TryGetValue(outputIndex, out var state)
                    && state.CallKind == ResponsesToolCallKind.CustomTool)
                {
                    var input = GetValue(item, "input") ?? new Dictionary<string, object?>();
                    var normalizedInput = ProtocolConverter.IsApplyPatchPublic(state.Name) && input is string patch
                        ? new Dictionary<string, object?> { ["patch"] = patch }
                        : input;
                    var argumentsJson = JsonSerializer.Serialize(NormalizeJsonValueForChat(normalizedInput), JsonOptions);
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
                                            && !IsServerExecutedNativeToolCallType(doneItemType)))
                                    && !toolStates.ContainsKey(i))
                                {
                                    throw new InvalidOperationException(
                                        $"Responses terminal output item '{doneItemType}' has no compatible Chat stream representation.");
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
                var failedResponse = TryAsObject(GetValue(payload, "response"), out var response)
                    ? new Dictionary<string, object?>(response, StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                responseId = StringValue(failedResponse, "id", responseId);
                responseModel = model ?? StringValue(failedResponse, "model", responseModel);
                failedResponse["id"] = responseId;
                failedResponse["object"] = "response";
                failedResponse["status"] = "failed";
                failedResponse["model"] = responseModel;
                failedResponse.TryAdd("output", outputByIndex.Values.Cast<object?>().ToList());
                failedResponse.TryAdd("usage", usage);
                result.UpstreamResponse = failedResponse;

                var error = GetValue(failedResponse, "error") ?? new Dictionary<string, object?>
                {
                    ["code"] = "response_failed",
                    ["message"] = "The upstream Responses stream failed."
                };
                yield return $"data: {JsonSerializer.Serialize(new Dictionary<string, object?> { ["error"] = error }, JsonOptions)}\n\n";
                yield break;
            }
        }

        var combinedText = string.Concat(textParts);
        var combinedReasoning = string.Concat(reasoningParts);

        // 确保 output 里有 message / reasoning 项，供 ProxyStreamService 的 ConvertResponse 提取
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

        result.UpstreamResponse = new Dictionary<string, object?>
        {
            ["id"] = responseId,
            ["object"] = "response",
            ["created_at"] = createdAt,
            ["status"] = finishStatus,
            ["model"] = responseModel,
            ["output"] = outputByIndex.Values.Cast<object?>().ToList(),
            ["usage"] = usage
        };

        foreach (var line in EnsureRoleChunk())
        {
            yield return line;
        }

        var finishReason = finishStatus == "incomplete"
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

        var promptTokens = ToInt(GetValue(usage, "input_tokens"));
        var completionTokens = ToInt(GetValue(usage, "output_tokens"));
        if (promptTokens > 0 || completionTokens > 0)
        {
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

    private static object? NormalizeJsonValueForChat(object? value)
    {
        if (value is Dictionary<string, object?> dict)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, val) in dict)
            {
                result[key] = NormalizeJsonValueForChat(val);
            }
            return result;
        }

        if (value is List<object?> list)
        {
            return list.Select(NormalizeJsonValueForChat).ToList();
        }

        return value;
    }

    private static string MissingSuffix(string emitted, string complete)
        => complete.StartsWith(emitted, StringComparison.Ordinal) ? complete[emitted.Length..] : complete;

    private static List<object?> BuildResponsesMessageContent(
        string text,
        IReadOnlyCollection<string> refusalParts,
        IReadOnlyCollection<object?> annotations)
    {
        var content = new List<object?>();
        if (text.Length > 0)
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "output_text",
                ["text"] = text,
                ["annotations"] = annotations.ToList()
            });
        }

        if (refusalParts.Count > 0)
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "refusal",
                ["refusal"] = string.Concat(refusalParts)
            });
        }

        return content;
    }

    private static bool IsNativeMcpItemType(string itemType)
        => itemType is "mcp_call" or "mcp_list_tools" or "mcp_approval_request" or "mcp_approval_response";

    private static bool IsResponsesNativeToolCallType(string itemType)
        => itemType.EndsWith("_call", StringComparison.Ordinal)
            || itemType is "mcp_list_tools" or "mcp_approval_request" or "mcp_approval_response";

    private static bool IsServerExecutedNativeToolCallType(string itemType)
        => itemType is "web_search_call" or "file_search_call" or "code_interpreter_call" or "image_generation_call";

    private static bool TryGetClientToolCallInfo(
        Dictionary<string, object?> item,
        out string name,
        out ResponsesToolCallKind callKind,
        out string argumentField)
    {
        var itemType = StringValue(item, "type", string.Empty);
        name = StringValue(item, "name", string.Empty);
        argumentField = "arguments";
        callKind = ProtocolConverter.GetResponsesToolCallKind(name);
        if (itemType == "function_call")
        {
            return name.Length > 0;
        }

        if (itemType == "custom_tool_call")
        {
            callKind = ResponsesToolCallKind.CustomTool;
            argumentField = "input";
            return name.Length > 0;
        }

        var defaultName = itemType switch
        {
            "tool_search_call" => "tool_search",
            "apply_patch_call" => "apply_patch",
            "local_shell_call" => "local_shell",
            "shell_call" => "shell",
            "computer_call" or "computer_use_call" => "computer_use",
            _ => string.Empty
        };
        if (defaultName.Length == 0)
        {
            return false;
        }

        name = name.Length > 0 ? name : defaultName;
        argumentField = itemType == "tool_search_call" ? "arguments" : "input";
        callKind = itemType == "apply_patch_call"
            ? ResponsesToolCallKind.CustomTool
            : ResponsesToolCallKind.NativeTool;
        return itemType != "tool_search_call"
            || !item.TryGetValue("execution", out var execution)
            || string.Equals(execution?.ToString(), "client", StringComparison.Ordinal);
    }

    private static bool TryGetToolArguments(Dictionary<string, object?> item, out string argumentsJson)
    {
        argumentsJson = string.Empty;
        foreach (var key in new[] { "arguments", "input", "action" })
        {
            if (!item.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            argumentsJson = value is string text && LooksLikeJson(text)
                ? text
                : JsonSerializer.Serialize(NormalizeJsonValueForChat(value), JsonOptions);
            return true;
        }

        return false;
    }

    private static bool LooksLikeJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed class ChatToolStreamState
{
    public int ChatIndex { get; set; }
    public string CallId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ResponsesToolCallKind CallKind { get; set; } = ResponsesToolCallKind.Function;
    public StringBuilder? CustomInputBuilder { get; set; }
    public bool ArgumentsDeltaEmitted { get; set; }
}

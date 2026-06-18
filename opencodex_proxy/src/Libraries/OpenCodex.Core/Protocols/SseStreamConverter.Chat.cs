using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    public static async IAsyncEnumerable<string> ChatToResponsesEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ChatToResponsesEvents(
            upstreamLines,
            model,
            result,
            SkipToolNames: null,
            SkipResponseCreated: false,
            InitialSequenceNumber: 0,
            InitialOutputIndex: 0,
            cancellationToken))
        {
            yield return line;
        }
    }

    public static async IAsyncEnumerable<string> ChatToResponsesEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        IReadOnlySet<string>? SkipToolNames,
        bool SkipResponseCreated,
        int InitialSequenceNumber,
        int InitialOutputIndex,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var responseId = $"resp_{Guid.NewGuid():N}";
        var messageItemId = $"msg_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var responseModel = model;
        var textParts = new List<string>();
        var refusalParts = new List<string>();
        var usage = new Dictionary<string, object?>(StringComparer.Ordinal);
        var completionId = string.Empty;
        object? completionCreated = null;
        var finishReason = "stop";
        var textStarted = false;
        var reasoningParts = new List<string>();
        var reasoningStarted = false;
        var reasoningOutputIndex = (int?)null;
        var reasoningItemId = $"rs_{Guid.NewGuid():N}";
        var messageOutputIndex = (int?)null;
        var nextOutputIndex = InitialOutputIndex;
        var sequenceNumber = InitialSequenceNumber;
        var toolCalls = new SortedDictionary<int, ToolCallAggregate>();
        var toolStreamStates = new Dictionary<int, ToolStreamState>();
        var outputByIndex = new SortedDictionary<int, Dictionary<string, object?>>();

        string Emit(string eventName, Dictionary<string, object?> payload)
        {
            var enriched = new Dictionary<string, object?>(payload, StringComparer.Ordinal)
            {
                ["type"] = eventName,
                ["sequence_number"] = sequenceNumber++
            };
            return $"event: {eventName}\ndata: {JsonSerializer.Serialize(enriched, JsonOptions)}\n\n";
        }

        int AllocateOutputIndex()
        {
            return nextOutputIndex++;
        }

        List<string> EnsureMessageStarted()
        {
            if (textStarted)
            {
                return [];
            }

            textStarted = true;
            messageOutputIndex = AllocateOutputIndex();
            return
            [
                Emit(
                    "response.output_item.added",
                    new Dictionary<string, object?>
                    {
                        ["output_index"] = messageOutputIndex,
                        ["item"] = new Dictionary<string, object?>
                        {
                            ["id"] = messageItemId,
                            ["type"] = "message",
                            ["status"] = "in_progress",
                            ["role"] = "assistant",
                            ["content"] = new List<object?>()
                        }
                    }),
                Emit(
                    "response.content_part.added",
                    new Dictionary<string, object?>
                    {
                        ["item_id"] = messageItemId,
                        ["output_index"] = messageOutputIndex,
                        ["content_index"] = 0,
                        ["part"] = new Dictionary<string, object?>
                        {
                            ["type"] = "output_text",
                            ["text"] = string.Empty,
                            ["annotations"] = new List<object?>()
                        }
                    })
            ];
        }

        ToolStreamState EnsureToolStreamState(int index)
        {
            if (!toolStreamStates.TryGetValue(index, out var state))
            {
                state = new ToolStreamState();
                toolStreamStates[index] = state;
            }

            state.OutputIndex ??= AllocateOutputIndex();
            state.ItemId ??= $"fc_{Guid.NewGuid():N}";
            return state;
        }

        List<string> EnsureReasoningStarted()
        {
            if (reasoningStarted)
            {
                return [];
            }

            reasoningStarted = true;
            reasoningOutputIndex = AllocateOutputIndex();
            return
            [
                Emit(
                    "response.output_item.added",
                    new Dictionary<string, object?>
                    {
                        ["output_index"] = reasoningOutputIndex,
                        ["item"] = new Dictionary<string, object?>
                        {
                            ["id"] = reasoningItemId,
                            ["type"] = "reasoning",
                            ["status"] = "in_progress",
                            ["summary"] = new List<object?>()
                        }
                    }),
                Emit(
                    "response.reasoning_summary_part.added",
                    new Dictionary<string, object?>
                    {
                        ["item_id"] = reasoningItemId,
                        ["output_index"] = reasoningOutputIndex,
                        ["summary_index"] = 0,
                        ["part"] = new Dictionary<string, object?>
                        {
                            ["type"] = "summary_text",
                            ["text"] = string.Empty
                        }
                    })
            ];
        }

        // 提前启动上游HTTP请求，避免延迟
        var enumerator = ParseEvents(upstreamLines, cancellationToken).GetAsyncEnumerator(cancellationToken);

        if (!SkipResponseCreated)
        {
            Console.Error.WriteLine($"[OCXP-DEBUG] ChatToResponsesEvents: yielding response.created (before upstream read)");
            yield return Emit(
                "response.created",
                new Dictionary<string, object?>
                {
                    ["response"] = new Dictionary<string, object?>
                    {
                        ["id"] = responseId,
                        ["object"] = "response",
                        ["created_at"] = createdAt,
                        ["status"] = "in_progress",
                        ["model"] = responseModel,
                        ["output"] = new List<object?>()
                    }
                });
            yield return Emit(
                "response.in_progress",
                new Dictionary<string, object?>
                {
                    ["response"] = new Dictionary<string, object?>
                    {
                        ["id"] = responseId,
                        ["object"] = "response",
                        ["created_at"] = createdAt,
                        ["status"] = "in_progress",
                        ["model"] = responseModel,
                        ["output"] = new List<object?>()
                    }
                });
            Console.Error.WriteLine($"[OCXP-DEBUG] ChatToResponsesEvents: yielded response.in_progress, now entering ParseEvents loop (will start upstream read)...");
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
                if (!TryAsObject(GetValue(choice, "delta"), out var delta))
                {
                    continue;
                }

                var text = StringValue(delta, "content", string.Empty);
                if (text.Length > 0)
                {
                    foreach (var line in EnsureMessageStarted())
                    {
                        yield return line;
                    }

                    textParts.Add(text);
                    yield return Emit(
                        "response.output_text.delta",
                        new Dictionary<string, object?>
                        {
                            ["item_id"] = messageItemId,
                            ["output_index"] = messageOutputIndex,
                            ["content_index"] = 0,
                            ["delta"] = text,
                            ["logprobs"] = new List<object?>()
                        });
                }

                var refusal = StringValue(delta, "refusal", string.Empty);
                if (refusal.Length > 0)
                {
                    foreach (var line in EnsureMessageStarted())
                    {
                        yield return line;
                    }

                    textParts.Add(refusal);
                    refusalParts.Add(refusal);
                    yield return Emit(
                        "response.output_text.delta",
                        new Dictionary<string, object?>
                        {
                            ["item_id"] = messageItemId,
                            ["output_index"] = messageOutputIndex,
                            ["content_index"] = 0,
                            ["delta"] = refusal,
                            ["logprobs"] = new List<object?>()
                        });
                }

                var reasoningText = StringValue(delta, "reasoning_content", string.Empty);
                if (reasoningText.Length > 0)
                {
                    foreach (var line in EnsureReasoningStarted())
                    {
                        yield return line;
                    }

                    reasoningParts.Add(reasoningText);
                    yield return Emit(
                        "response.reasoning_summary_text.delta",
                        new Dictionary<string, object?>
                        {
                            ["item_id"] = reasoningItemId,
                            ["output_index"] = reasoningOutputIndex,
                            ["summary_index"] = 0,
                            ["delta"] = reasoningText
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
                    if (!toolCalls.TryGetValue(index, out var aggregate))
                    {
                        aggregate = new ToolCallAggregate();
                        toolCalls[index] = aggregate;
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

                    var state = EnsureToolStreamState(index);
                    if (!state.ItemAdded)
                    {
                        state.ItemAdded = true;
                        yield return Emit(
                            "response.output_item.added",
                            new Dictionary<string, object?>
                            {
                                ["output_index"] = state.OutputIndex,
                                ["item"] = new Dictionary<string, object?>
                                {
                                    ["id"] = state.ItemId,
                                    ["type"] = "function_call",
                                    ["status"] = "in_progress",
                                    ["call_id"] = aggregate.Id,
                                    ["name"] = aggregate.Name,
                                    ["arguments"] = string.Empty
                                }
                            });
                    }

                    if (aggregate.Arguments.Length <= state.StreamedArgumentsLength)
                    {
                        continue;
                    }

                    // apply_patch is a FREEFORM tool: upstream sends JSON like {"patch":"..."}.
                    // The client expects raw patch text, not JSON fragments.
                    // Skip delta streaming for apply_patch; the done event carries the unwrapped text.
                    if (ProtocolConverter.IsApplyPatchPublic(aggregate.Name))
                    {
                        state.StreamedArgumentsLength = aggregate.Arguments.Length;
                        continue;
                    }

                    var deltaText = aggregate.Arguments[state.StreamedArgumentsLength..];
                    state.StreamedArgumentsLength = aggregate.Arguments.Length;
                    yield return Emit(
                        "response.function_call_arguments.delta",
                        new Dictionary<string, object?>
                        {
                            ["item_id"] = state.ItemId,
                            ["output_index"] = state.OutputIndex,
                            ["delta"] = deltaText
                        });
                }
            }
        }

        var combinedText = string.Concat(textParts);
        var combinedReasoning = string.Concat(reasoningParts);
        var reconstructedToolCalls = new List<object?>();
        foreach (var (index, aggregate) in toolCalls.ToList())
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
            toolCalls[index] = aggregate;
        }

        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = combinedText,
            ["tool_calls"] = reconstructedToolCalls
            ,["reasoning_content"] = combinedReasoning
            ,["refusal"] = string.Concat(refusalParts)
        };
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
                    ["message"] = message,
                    ["finish_reason"] = finishReason
                }
            },
            ["usage"] = usage
        };

        if (combinedReasoning.Length > 0)
        {
            var reasoningItem = new Dictionary<string, object?>
            {
                ["id"] = reasoningItemId,
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
            yield return Emit(
                "response.reasoning_summary_text.done",
                new Dictionary<string, object?>
                {
                    ["item_id"] = reasoningItemId,
                    ["output_index"] = reasoningOutputIndex,
                    ["summary_index"] = 0,
                    ["text"] = combinedReasoning
                });
            yield return Emit(
                "response.reasoning_summary_part.done",
                new Dictionary<string, object?>
                {
                    ["item_id"] = reasoningItemId,
                    ["output_index"] = reasoningOutputIndex,
                    ["summary_index"] = 0,
                    ["part"] = new Dictionary<string, object?>
                    {
                        ["type"] = "summary_text",
                        ["text"] = combinedReasoning
                    }
                });
            outputByIndex[reasoningOutputIndex ?? AllocateOutputIndex()] = reasoningItem;
            yield return Emit(
                "response.output_item.done",
                new Dictionary<string, object?>
                {
                    ["output_index"] = reasoningOutputIndex,
                    ["item"] = reasoningItem
                });
        }

        if (combinedText.Length > 0)
        {
            var outputText = new Dictionary<string, object?>
            {
                ["type"] = "output_text",
                ["text"] = combinedText,
                ["annotations"] = new List<object?>()
            };
            var messageItem = new Dictionary<string, object?>
            {
                ["id"] = messageItemId,
                ["type"] = "message",
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new List<object?> { outputText }
            };
            yield return Emit(
                "response.output_text.done",
                new Dictionary<string, object?>
                {
                    ["item_id"] = messageItemId,
                    ["output_index"] = messageOutputIndex,
                    ["content_index"] = 0,
                    ["text"] = combinedText,
                    ["logprobs"] = new List<object?>()
                });
            yield return Emit(
                "response.content_part.done",
                new Dictionary<string, object?>
                {
                    ["item_id"] = messageItemId,
                    ["output_index"] = messageOutputIndex,
                    ["content_index"] = 0,
                    ["part"] = outputText
                });
            outputByIndex[messageOutputIndex ?? AllocateOutputIndex()] = messageItem;
            yield return Emit(
                "response.output_item.done",
                new Dictionary<string, object?>
                {
                    ["output_index"] = messageOutputIndex,
                    ["item"] = messageItem
                });
        }

        foreach (var (index, aggregate) in toolCalls)
        {
            if (string.IsNullOrEmpty(aggregate.Id) || string.IsNullOrEmpty(aggregate.Name))
            {
                continue;
            }

            if (SkipToolNames?.Contains(aggregate.Name) is true)
            {
                continue;
            }

            var state = toolStreamStates.TryGetValue(index, out var existingState)
                ? existingState
                : EnsureToolStreamState(index);
            var itemId = state.ItemId ?? $"fc_{Guid.NewGuid():N}";
            var outputIndex = state.OutputIndex ?? AllocateOutputIndex();
            var functionItem = ProtocolConverter.ResponsesToolCallItemFromToolCall(
                aggregate.Id,
                aggregate.Name,
                aggregate.Arguments,
                itemId: itemId);
            var functionItemType = functionItem.TryGetValue("type", out var itemType)
                ? itemType?.ToString()
                : null;
            if (!state.ItemAdded && functionItemType == "function_call")
            {
                yield return Emit(
                    "response.output_item.added",
                    new Dictionary<string, object?>
                    {
                        ["output_index"] = outputIndex,
                        ["item"] = new Dictionary<string, object?>
                        {
                            ["id"] = itemId,
                            ["type"] = "function_call",
                            ["status"] = "in_progress",
                            ["call_id"] = aggregate.Id,
                            ["name"] = functionItem.TryGetValue("name", out var itemName)
                                ? itemName
                                : aggregate.Name,
                            ["namespace"] = functionItem.TryGetValue("namespace", out var itemNamespace)
                                ? itemNamespace
                                : null,
                            ["arguments"] = string.Empty
                        }
                    });
            }

            outputByIndex[outputIndex] = functionItem;
            if (functionItemType == "function_call")
            {
                yield return Emit(
                    "response.function_call_arguments.done",
                    new Dictionary<string, object?>
                    {
                        ["item_id"] = itemId,
                        ["output_index"] = outputIndex,
                        ["arguments"] = functionItem.TryGetValue("arguments", out var itemArguments)
                            ? itemArguments
                            : aggregate.Arguments
                    });
            }

            yield return Emit(
                "response.output_item.done",
                new Dictionary<string, object?>
                {
                    ["output_index"] = outputIndex,
                    ["item"] = functionItem
                });
        }

        yield return Emit(
            "response.completed",
            new Dictionary<string, object?>
            {
                ["response"] = new Dictionary<string, object?>
                {
                    ["id"] = responseId,
                    ["object"] = "response",
                    ["created_at"] = createdAt,
                    ["status"] = finishReason == "length" ? "incomplete" : "completed",
                    ["model"] = responseModel,
                    ["output"] = outputByIndex.Values.Cast<object?>().ToList(),
                    ["usage"] = ChatUsageToResponsesUsage(usage),
                    ["end_turn"] = true
                    ,["incomplete_details"] = finishReason == "length"
                        ? new Dictionary<string, object?> { ["reason"] = "max_output_tokens" }
                        : null
                    ,["parallel_tool_calls"] = true,
                    ["error"] = null,
                    ["truncation"] = "disabled"
                }
            });
    }
}

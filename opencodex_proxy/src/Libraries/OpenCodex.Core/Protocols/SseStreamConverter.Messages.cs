using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    public static async IAsyncEnumerable<string> MessagesToResponsesEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in MessagesToResponsesEvents(
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

    public static async IAsyncEnumerable<string> MessagesToResponsesEvents(
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
        var reasoningParts = new List<string>();
        var contentBlocks = new SortedDictionary<int, Dictionary<string, object?>>();
        var inputJsonParts = new Dictionary<int, List<string>>();
        var stopReason = "stop";
        var usage = new Dictionary<string, object?>(StringComparer.Ordinal);
        var textStarted = false;
        var messageOutputIndex = (int?)null;
        var reasoningStarted = false;
        var reasoningOutputIndex = (int?)null;
        var reasoningItemId = $"rs_{Guid.NewGuid():N}";
        var sequenceNumber = InitialSequenceNumber;
        var toolStates = new Dictionary<int, ToolStreamState>();
        var nextOutputIndex = InitialOutputIndex;

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

        ToolStreamState EnsureToolState(int index)
        {
            if (!toolStates.TryGetValue(index, out var state))
            {
                state = new ToolStreamState();
                toolStates[index] = state;
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
                            ["text"] = string.Empty
                        }
                    })
            ];
        }

        if (!SkipResponseCreated)
        {
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
        }

        await foreach (var sseEvent in ParseEvents(upstreamLines, cancellationToken))
        {
            if (!TryAsObject(sseEvent.Data, out var payload))
            {
                continue;
            }

            var eventType = StringValue(payload, "type", sseEvent.EventName);
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
                            foreach (var emitted in EnsureReasoningStarted())
                            {
                                yield return emitted;
                            }

                            reasoningParts.Add(initialThinking);
                            yield return Emit(
                                "response.reasoning_summary_text.delta",
                                new Dictionary<string, object?>
                                {
                                    ["item_id"] = reasoningItemId,
                                    ["output_index"] = reasoningOutputIndex,
                                    ["summary_index"] = 0,
                                    ["delta"] = initialThinking
                                });
                        }
                    }
                    else if (blockType == "tool_use")
                    {
                        var toolName = StringValue(block, "name", string.Empty);
                        if (SkipToolNames?.Contains(toolName) is true)
                        {
                            continue;
                        }

                        var state = EnsureToolState(index);
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
                                        ["call_id"] = GetValue(block, "id"),
                                        ["name"] = GetValue(block, "name"),
                                        ["arguments"] = string.Empty
                                    }
                                });
                        }
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

                    foreach (var line in EnsureReasoningStarted())
                    {
                        yield return line;
                    }

                    reasoningParts.Add(thinking);
                    block["thinking"] = $"{StringValue(block, "thinking", string.Empty)}{thinking}";
                    yield return Emit(
                        "response.reasoning_summary_text.delta",
                        new Dictionary<string, object?>
                        {
                            ["item_id"] = reasoningItemId,
                            ["output_index"] = reasoningOutputIndex,
                            ["summary_index"] = 0,
                            ["delta"] = thinking
                        });
                }
                else if (deltaType == "text_delta")
                {
                    var text = StringValue(delta, "text", string.Empty);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    foreach (var line in EnsureMessageStarted())
                    {
                        yield return line;
                    }

                    textParts.Add(text);
                    block["text"] = $"{StringValue(block, "text", string.Empty)}{text}";
                    yield return Emit(
                        "response.output_text.delta",
                        new Dictionary<string, object?>
                        {
                            ["item_id"] = messageItemId,
                            ["output_index"] = messageOutputIndex,
                            ["content_index"] = 0,
                            ["delta"] = text
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

                    // Stream the delta as a function_call_arguments.delta event.
                    if (partialJson.Length > 0)
                    {
                        if (SkipToolNames?.Contains(StringValue(block, "name", string.Empty)) is true)
                        {
                            continue;
                        }

                        var state = EnsureToolState(index);
                        yield return Emit(
                            "response.function_call_arguments.delta",
                            new Dictionary<string, object?>
                            {
                                ["item_id"] = state.ItemId,
                                ["output_index"] = state.OutputIndex,
                                ["delta"] = partialJson
                            });
                    }
                }

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

        foreach (var (index, parts) in inputJsonParts)
        {
            if (!contentBlocks.TryGetValue(index, out var block))
            {
                block = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "tool_use"
                };
                contentBlocks[index] = block;
            }

            block["input"] = ParseJsonObject(string.Concat(parts));
        }

        var orderedBlocks = contentBlocks.Values.Cast<object?>().ToList();
        result.UpstreamResponse = new Dictionary<string, object?>
        {
            ["id"] = $"msg_{Guid.NewGuid():N}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = responseModel,
            ["content"] = orderedBlocks,
            ["stop_reason"] = stopReason,
            ["usage"] = usage
        };

        var output = new List<object?>();
        var combinedReasoning = string.Concat(reasoningParts);
        var combinedText = string.Concat(textParts);
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
            output.Add(reasoningItem);
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
            foreach (var line in EnsureMessageStarted())
            {
                yield return line;
            }

            var outputText = new Dictionary<string, object?>
            {
                ["type"] = "output_text",
                ["text"] = combinedText
            };
            var messageItem = new Dictionary<string, object?>
            {
                ["id"] = messageItemId,
                ["type"] = "message",
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new List<object?> { outputText }
            };
            output.Add(messageItem);
            yield return Emit(
                "response.output_text.done",
                new Dictionary<string, object?>
                {
                    ["item_id"] = messageItemId,
                    ["output_index"] = messageOutputIndex,
                    ["content_index"] = 0,
                    ["text"] = combinedText
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
            yield return Emit(
                "response.output_item.done",
                new Dictionary<string, object?>
                {
                    ["output_index"] = messageOutputIndex,
                    ["item"] = messageItem
                });
        }

        // Finalize tool_use items: emit done events and build output items.
        foreach (var (index, block) in contentBlocks)
        {
            if (StringValue(block, "type", string.Empty) != "tool_use")
            {
                continue;
            }

            var callId = GetValue(block, "id") ?? $"call_{Guid.NewGuid():N}";
            var name = GetValue(block, "name");
            var toolName = name?.ToString() ?? string.Empty;
            if (SkipToolNames?.Contains(toolName) is true)
            {
                continue;
            }

            var state = toolStates.TryGetValue(index, out var existingState)
                ? existingState
                : EnsureToolState(index);
            var itemId = state.ItemId ?? $"fc_{Guid.NewGuid():N}";
            var outputIndex = state.OutputIndex ?? AllocateOutputIndex();
            var arguments = WebSearchPayload.JsonDumps(GetValue(block, "input") ?? new Dictionary<string, object?>());
            var functionItem = ProtocolConverter.ResponsesToolCallItemFromToolCall(
                callId,
                name,
                arguments,
                itemId: itemId);

            yield return Emit(
                "response.function_call_arguments.done",
                new Dictionary<string, object?>
                {
                    ["item_id"] = itemId,
                    ["output_index"] = outputIndex,
                    ["arguments"] = functionItem.TryGetValue("arguments", out var itemArguments)
                        ? itemArguments
                        : arguments
                });
            yield return Emit(
                "response.output_item.done",
                new Dictionary<string, object?>
                {
                    ["output_index"] = outputIndex,
                    ["item"] = functionItem
                });
            output.Add(functionItem);
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
                    ["status"] = "completed",
                    ["model"] = responseModel,
                    ["output"] = output,
                    ["usage"] = MessagesUsageToResponsesUsage(usage),
                    ["end_turn"] = true,
                    ["parallel_tool_calls"] = true,
                    ["error"] = null,
                    ["truncation"] = "disabled"
                }
            });
    }
}

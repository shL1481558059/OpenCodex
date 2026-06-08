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
        var usage = new Dictionary<string, object?>(StringComparer.Ordinal);
        var completionId = string.Empty;
        object? completionCreated = null;
        var finishReason = "stop";
        var textStarted = false;
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
        }

        await foreach (var sseEvent in ParseEvents(upstreamLines, cancellationToken))
        {
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
                            ["delta"] = text
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

        if (combinedText.Length > 0)
        {
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
            if (!state.ItemAdded)
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
                            ["name"] = aggregate.Name,
                            ["arguments"] = string.Empty
                        }
                    });
            }

            var functionItem = new Dictionary<string, object?>
            {
                ["id"] = itemId,
                ["type"] = "function_call",
                ["status"] = "completed",
                ["call_id"] = aggregate.Id,
                ["name"] = aggregate.Name,
                ["arguments"] = aggregate.Arguments
            };
            outputByIndex[outputIndex] = functionItem;
            yield return Emit(
                "response.function_call_arguments.done",
                new Dictionary<string, object?>
                {
                    ["item_id"] = itemId,
                    ["output_index"] = outputIndex,
                    ["arguments"] = aggregate.Arguments
                });
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
                }
            });
    }
}

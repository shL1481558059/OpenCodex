using System.Text.Json;

namespace OpenCodex.Api.Protocols;

public static partial class SseStreamConverter
{
    public static async IAsyncEnumerable<string> MessagesToResponsesEvents(
        IAsyncEnumerable<string> upstreamLines,
        string? model,
        ConvertedStreamResult result,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var responseId = $"resp_{Guid.NewGuid():N}";
        var messageItemId = $"msg_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var responseModel = model;
        var textParts = new List<string>();
        var contentBlocks = new SortedDictionary<int, Dictionary<string, object?>>();
        var inputJsonParts = new Dictionary<int, List<string>>();
        var stopReason = "stop";
        var usage = new Dictionary<string, object?>(StringComparer.Ordinal);
        var textStarted = false;
        var sequenceNumber = 0;

        string Emit(string eventName, Dictionary<string, object?> payload)
        {
            var enriched = new Dictionary<string, object?>(payload, StringComparer.Ordinal)
            {
                ["type"] = eventName,
                ["sequence_number"] = sequenceNumber++
            };
            return $"event: {eventName}\ndata: {JsonSerializer.Serialize(enriched, JsonOptions)}\n\n";
        }

        List<string> EnsureMessageStarted()
        {
            if (textStarted)
            {
                return [];
            }

            textStarted = true;
            return
            [
                Emit(
                    "response.output_item.added",
                    new Dictionary<string, object?>
                    {
                        ["output_index"] = 0,
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
                        ["output_index"] = 0,
                        ["content_index"] = 0,
                        ["part"] = new Dictionary<string, object?>
                        {
                            ["type"] = "output_text",
                            ["text"] = string.Empty
                        }
                    })
            ];
        }

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
                    contentBlocks[index] = new Dictionary<string, object?>(block, StringComparer.Ordinal);
                    if (StringValue(block, "type", string.Empty) == "text")
                    {
                        var initialText = StringValue(block, "text", string.Empty);
                        if (initialText.Length > 0)
                        {
                            textParts.Add(initialText);
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
                if (deltaType == "text_delta")
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
                            ["output_index"] = 0,
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

                    parts.Add(StringValue(delta, "partial_json", string.Empty));
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
        var combinedText = string.Concat(textParts);
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
            output.Add(messageItem);
            yield return Emit(
                "response.output_text.done",
                new Dictionary<string, object?>
                {
                    ["item_id"] = messageItemId,
                    ["output_index"] = 0,
                    ["content_index"] = 0,
                    ["text"] = combinedText
                });
            yield return Emit(
                "response.content_part.done",
                new Dictionary<string, object?>
                {
                    ["item_id"] = messageItemId,
                    ["output_index"] = 0,
                    ["content_index"] = 0,
                    ["part"] = outputText
                });
            yield return Emit(
                "response.output_item.done",
                new Dictionary<string, object?>
                {
                    ["output_index"] = 0,
                    ["item"] = messageItem
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
                    ["status"] = "completed",
                    ["model"] = responseModel,
                    ["output"] = output,
                    ["usage"] = MessagesUsageToResponsesUsage(usage),
                    ["end_turn"] = true
                }
            });
    }
}

using System.Text;

namespace OpenCodex.Core.Protocols;

internal sealed class ChatStreamResponseAccumulator : IStreamResponseAccumulator
{
    private static readonly string[] ErrorFallbackFields = ["type", "code", "message", "param"];

    private readonly StreamCaptureBudget _budget;
    private readonly SortedDictionary<int, ChoiceState> _choices = [];
    private string? _id;
    private object? _created;
    private string? _model;
    private string? _systemFingerprint;
    private string? _serviceTier;
    private Dictionary<string, object?>? _usage;
    private object? _error;

    public ChatStreamResponseAccumulator(StreamCaptureBudget budget)
    {
        _budget = budget;
    }

    public bool IsComplete { get; private set; }

    public void Accept(SseEvent streamEvent)
    {
        if (streamEvent.Data is string text && text == "[DONE]")
        {
            IsComplete = true;
            return;
        }

        if (!StreamCaptureValues.TryObject(streamEvent.Data, out var payload))
        {
            return;
        }

        CaptureEnvelope(payload);

        if (payload.TryGetValue("error", out var error) && error is not null)
        {
            _error = CaptureBoundedObject(error, ErrorFallbackFields);
            IsComplete = true;
        }

        if (payload.TryGetValue("usage", out var usage)
            && StreamCaptureValues.TryObject(usage, out var usageObject))
        {
            _usage = CaptureUsage(usageObject);
        }

        if (!payload.TryGetValue("choices", out var choicesValue)
            || !StreamCaptureValues.TryList(choicesValue, out var choices))
        {
            return;
        }

        foreach (var rawChoice in choices)
        {
            if (!StreamCaptureValues.TryObject(rawChoice, out var choice))
            {
                continue;
            }

            var index = StreamCaptureValues.Int(choice, "index");
            if (!_choices.TryGetValue(index, out var state))
            {
                if (_choices.Count >= _budget.MaxCollectionItems)
                {
                    _budget.MarkTruncated();
                    continue;
                }

                state = new ChoiceState(index);
                _choices[index] = state;
            }

            CaptureChoice(state, choice);
        }
    }

    public Dictionary<string, object?>? BuildResponse()
    {
        if (_id is null
            && _created is null
            && _model is null
            && _systemFingerprint is null
            && _serviceTier is null
            && _choices.Count == 0
            && _usage is null
            && _error is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddIfPresent(result, "id", _id);
        if (_id is not null || _created is not null || _model is not null || _choices.Count > 0)
        {
            result["object"] = "chat.completion";
        }

        AddIfPresent(result, "created", _created);
        AddIfPresent(result, "model", _model);
        AddIfPresent(result, "system_fingerprint", _systemFingerprint);
        AddIfPresent(result, "service_tier", _serviceTier);

        if (_choices.Count > 0)
        {
            result["choices"] = _choices.Values
                .Select(BuildChoice)
                .Cast<object?>()
                .ToList();
        }

        if (_usage is not null)
        {
            result["usage"] = StreamCaptureValues.CloneObject(_usage);
        }

        if (_error is not null)
        {
            result["error"] = StreamCaptureValues.CloneValue(_error);
        }

        return result;
    }

    private void CaptureEnvelope(IReadOnlyDictionary<string, object?> payload)
    {
        _id ??= CaptureIdentifier(StreamCaptureValues.String(payload, "id"));
        _model ??= CaptureIdentifier(StreamCaptureValues.String(payload, "model"));
        _systemFingerprint ??= CaptureIdentifier(StreamCaptureValues.String(payload, "system_fingerprint"));
        _serviceTier ??= CaptureIdentifier(StreamCaptureValues.String(payload, "service_tier"));
        if (_created is null && payload.TryGetValue("created", out var created))
        {
            _created = created;
        }
    }

    private void CaptureChoice(ChoiceState state, IReadOnlyDictionary<string, object?> choice)
    {
        if (choice.TryGetValue("finish_reason", out var finishReason)
            && finishReason is not null)
        {
            state.FinishReason = StreamCaptureValues.CloneValue(finishReason);
        }

        if (choice.TryGetValue("logprobs", out var logprobs)
            && StreamCaptureValues.TryObject(logprobs, out var logprobsObject))
        {
            CaptureLogprobs(state, logprobsObject);
        }

        if (!choice.TryGetValue("delta", out var deltaValue)
            || !StreamCaptureValues.TryObject(deltaValue, out var delta))
        {
            return;
        }

        state.Role ??= StreamCaptureValues.String(delta, "role");
        _budget.Append(state.Content, StreamCaptureValues.String(delta, "content"));
        _budget.Append(state.ReasoningContent, StreamCaptureValues.String(delta, "reasoning_content"));
        _budget.Append(state.Refusal, StreamCaptureValues.String(delta, "refusal"));

        if (!delta.TryGetValue("tool_calls", out var toolCallsValue)
            || !StreamCaptureValues.TryList(toolCallsValue, out var toolCalls))
        {
            return;
        }

        foreach (var rawToolCall in toolCalls)
        {
            if (!StreamCaptureValues.TryObject(rawToolCall, out var toolCall))
            {
                continue;
            }

            var toolIndex = StreamCaptureValues.Int(toolCall, "index");
            if (!state.ToolCalls.TryGetValue(toolIndex, out var toolState))
            {
                if (state.ToolCalls.Count >= _budget.MaxCollectionItems)
                {
                    _budget.MarkTruncated();
                    continue;
                }

                toolState = new ToolCallState();
                state.ToolCalls[toolIndex] = toolState;
            }

            CaptureToolCall(toolState, toolCall);
        }
    }

    private void CaptureToolCall(ToolCallState state, IReadOnlyDictionary<string, object?> toolCall)
    {
        state.Type ??= StreamCaptureValues.String(toolCall, "type");
        AppendOnce(state.Id, StreamCaptureValues.String(toolCall, "id"));

        if (toolCall.TryGetValue("function", out var functionValue)
            && StreamCaptureValues.TryObject(functionValue, out var function))
        {
            AppendOnce(state.Name, StreamCaptureValues.String(function, "name"));
            _budget.Append(state.Arguments, StreamCaptureValues.String(function, "arguments"));
        }

        if (toolCall.TryGetValue("custom", out var customValue)
            && StreamCaptureValues.TryObject(customValue, out var custom))
        {
            state.Type ??= "custom";
            AppendOnce(state.Name, StreamCaptureValues.String(custom, "name"));
            _budget.Append(state.Arguments, StreamCaptureValues.String(custom, "input"));
        }
    }

    private void CaptureLogprobs(ChoiceState state, IReadOnlyDictionary<string, object?> logprobs)
    {
        state.Logprobs ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in logprobs)
        {
            if (StreamCaptureValues.TryList(value, out var incomingItems))
            {
                if (!state.Logprobs.TryGetValue(key, out var existingValue)
                    || !StreamCaptureValues.TryList(existingValue, out var existingItems))
                {
                    existingItems = [];
                    state.Logprobs[key] = existingItems;
                }

                foreach (var item in incomingItems)
                {
                    if (existingItems.Count >= _budget.MaxCollectionItems)
                    {
                        _budget.MarkTruncated();
                        break;
                    }

                    if (_budget.Fits(item))
                    {
                        existingItems.Add(StreamCaptureValues.CloneValue(item));
                    }
                }

                continue;
            }

            if (_budget.Fits(value))
            {
                state.Logprobs[key] = StreamCaptureValues.CloneValue(value);
            }
        }
    }

    private Dictionary<string, object?> BuildChoice(ChoiceState state)
    {
        var message = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = state.Role ?? "assistant",
            ["content"] = state.Content.ToString()
        };

        if (state.ReasoningContent.Length > 0)
        {
            message["reasoning_content"] = state.ReasoningContent.ToString();
        }

        if (state.Refusal.Length > 0)
        {
            message["refusal"] = state.Refusal.ToString();
        }

        if (state.ToolCalls.Count > 0)
        {
            message["tool_calls"] = state.ToolCalls.Values
                .Select(BuildToolCall)
                .Cast<object?>()
                .ToList();
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["index"] = state.Index,
            ["message"] = message,
            ["finish_reason"] = state.FinishReason
        };
        if (state.Logprobs is not null)
        {
            result["logprobs"] = StreamCaptureValues.CloneObject(state.Logprobs);
        }

        return result;
    }

    private static Dictionary<string, object?> BuildToolCall(ToolCallState state)
    {
        var type = state.Type ?? "function";
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = state.Id.ToString(),
            ["type"] = type
        };

        result[type == "custom" ? "custom" : "function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = state.Name.ToString(),
            [type == "custom" ? "input" : "arguments"] = state.Arguments.ToString()
        };
        return result;
    }

    private void AppendOnce(StringBuilder target, string? value)
    {
        if (target.Length == 0)
        {
            _budget.Append(target, value);
        }
    }

    private string? CaptureIdentifier(string? value)
    {
        const int maxIdentifierLength = 4096;
        if (value is null || value.Length <= maxIdentifierLength)
        {
            return value;
        }

        _budget.MarkTruncated();
        return value[..maxIdentifierLength];
    }

    private Dictionary<string, object?> CaptureUsage(IReadOnlyDictionary<string, object?> usage)
    {
        if (_budget.Fits(usage))
        {
            return StreamCaptureValues.CloneObject(usage);
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in usage)
        {
            if (result.Count >= 64)
            {
                break;
            }

            if (value is int or long or double or decimal)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private object? CaptureBoundedObject(object? value, IReadOnlyList<string> fallbackFields)
    {
        if (_budget.Fits(value))
        {
            return StreamCaptureValues.CloneValue(value);
        }

        if (!StreamCaptureValues.TryObject(value, out var objectValue))
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in fallbackFields)
        {
            if (!objectValue.TryGetValue(field, out var fieldValue))
            {
                continue;
            }

            if (field == "message" && fieldValue is string message)
            {
                var capturedMessage = new StringBuilder();
                _budget.Append(capturedMessage, message);
                result[field] = capturedMessage.ToString();
            }
            else if (fieldValue is string or int or long or double or decimal or bool)
            {
                result[field] = fieldValue;
            }
        }

        return result;
    }

    private static void AddIfPresent(
        IDictionary<string, object?> target,
        string key,
        object? value)
    {
        if (value is not null)
        {
            target[key] = StreamCaptureValues.CloneValue(value);
        }
    }

    private sealed class ChoiceState
    {
        public ChoiceState(int index)
        {
            Index = index;
        }

        public int Index { get; }

        public string? Role { get; set; }

        public StringBuilder Content { get; } = new();

        public StringBuilder ReasoningContent { get; } = new();

        public StringBuilder Refusal { get; } = new();

        public SortedDictionary<int, ToolCallState> ToolCalls { get; } = [];

        public object? FinishReason { get; set; }

        public Dictionary<string, object?>? Logprobs { get; set; }
    }

    private sealed class ToolCallState
    {
        public string? Type { get; set; }

        public StringBuilder Id { get; } = new();

        public StringBuilder Name { get; } = new();

        public StringBuilder Arguments { get; } = new();
    }
}

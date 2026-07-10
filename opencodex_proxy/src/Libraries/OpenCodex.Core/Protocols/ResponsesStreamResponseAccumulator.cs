using System.Text;

namespace OpenCodex.Core.Protocols;

internal sealed class ResponsesStreamResponseAccumulator : IStreamResponseAccumulator
{
    private static readonly string[] LoggableResponseFields =
    [
        "id",
        "object",
        "created_at",
        "completed_at",
        "status",
        "model",
        "output",
        "usage",
        "error",
        "incomplete_details",
        "service_tier"
    ];

    private static readonly string[] BaseOutputItemFields =
    [
        "id", "type", "status", "role", "name", "call_id"
    ];

    private static readonly string[] BaseContentPartFields =
    [
        "type", "text", "refusal"
    ];

    private readonly StreamCaptureBudget _budget;
    private readonly SortedDictionary<int, ResponseOutputState> _outputStates = [];
    private Dictionary<string, object?>? _terminalResponse;
    private Dictionary<string, object?>? _fallbackResponse;

    public ResponsesStreamResponseAccumulator(StreamCaptureBudget budget)
    {
        _budget = budget;
    }

    public bool IsComplete { get; private set; }

    public void Accept(SseEvent streamEvent)
    {
        if (!StreamCaptureValues.TryObject(streamEvent.Data, out var payload))
        {
            return;
        }

        var type = StreamCaptureValues.String(payload, "type") ?? streamEvent.EventName;
        var isTerminal = type is "response.completed" or "response.failed" or "response.incomplete";
        if (!isTerminal)
        {
            CaptureResponseEnvelope(payload);
        }

        if (type is "response.output_item.added" or "response.output_item.done"
            && payload.TryGetValue("item", out var item)
            && StreamCaptureValues.TryObject(item, out var itemObject))
        {
            var outputIndex = StreamCaptureValues.Int(payload, "output_index", _outputStates.Count);
            var state = GetOutputState(outputIndex);
            if (state is null)
            {
                return;
            }
            if (type == "response.output_item.done")
            {
                state.CaptureCompletedItem(itemObject);
            }
            else
            {
                state.CaptureAddedItem(itemObject);
            }
            return;
        }

        CaptureOutputDelta(type, payload);

        if (isTerminal)
        {
            IsComplete = true;
            if (payload.TryGetValue("response", out var response)
                && StreamCaptureValues.TryObject(response, out var responseObject))
            {
                _terminalResponse = ProjectResponse(responseObject);
            }
        }
    }

    public Dictionary<string, object?>? BuildResponse()
    {
        var response = _terminalResponse ?? _fallbackResponse;
        if (response is null)
        {
            return null;
        }

        var result = StreamCaptureValues.CloneObject(response);
        if ((!result.TryGetValue("output", out var output)
             || !StreamCaptureValues.TryList(output, out var outputItems)
             || outputItems.Count == 0)
            && _outputStates.Count > 0)
        {
            var reconstructedOutput = _outputStates.Values
                .Select(state => (object?)state.Build())
                .ToList();
            result["output"] = reconstructedOutput;
        }

        if (result.ContainsKey("model") && !result.ContainsKey("usage"))
        {
            result["usage"] = new Dictionary<string, object?>();
        }

        return result;
    }

    private void CaptureOutputDelta(string type, IReadOnlyDictionary<string, object?> payload)
    {
        var outputIndex = StreamCaptureValues.Int(payload, "output_index");
        var delta = StreamCaptureValues.String(payload, "delta");
        switch (type)
        {
            case "response.output_text.delta":
                GetOutputState(outputIndex)?
                    .AppendOutputText(StreamCaptureValues.Int(payload, "content_index"), delta);
                break;
            case "response.output_text.done":
                GetOutputState(outputIndex)?
                    .CaptureOutputTextDone(
                        StreamCaptureValues.Int(payload, "content_index"),
                        StreamCaptureValues.String(payload, "text"));
                break;
            case "response.reasoning_summary_text.delta":
                GetOutputState(outputIndex)?.AppendReasoningSummary(
                    StreamCaptureValues.Int(payload, "summary_index", StreamCaptureValues.Int(payload, "content_index")),
                    delta);
                break;
            case "response.reasoning_summary_text.done":
                GetOutputState(outputIndex)?.CaptureReasoningSummaryDone(
                    StreamCaptureValues.Int(payload, "summary_index", StreamCaptureValues.Int(payload, "content_index")),
                    StreamCaptureValues.String(payload, "text"));
                break;
            case "response.function_call_arguments.delta":
                GetOutputState(outputIndex)?.AppendToolPayload("function_call", "arguments", delta);
                break;
            case "response.function_call_arguments.done":
                GetOutputState(outputIndex)?.CaptureToolPayloadDone(
                    "function_call",
                    "arguments",
                    StreamCaptureValues.String(payload, "arguments"));
                break;
            case "response.custom_tool_call_input.delta":
                GetOutputState(outputIndex)?.AppendToolPayload("custom_tool_call", "input", delta);
                break;
            case "response.custom_tool_call_input.done":
                GetOutputState(outputIndex)?.CaptureToolPayloadDone(
                    "custom_tool_call",
                    "input",
                    StreamCaptureValues.String(payload, "input"));
                break;
            case "response.refusal.delta":
                GetOutputState(outputIndex)?
                    .AppendRefusal(StreamCaptureValues.Int(payload, "content_index"), delta);
                break;
            case "response.refusal.done":
                GetOutputState(outputIndex)?
                    .CaptureRefusalDone(
                        StreamCaptureValues.Int(payload, "content_index"),
                        StreamCaptureValues.String(payload, "refusal"));
                break;
            case "response.content_part.added":
            case "response.content_part.done":
                if (payload.TryGetValue("part", out var part)
                    && StreamCaptureValues.TryObject(part, out var partObject))
                {
                    GetOutputState(outputIndex)?.CaptureContentPart(
                        StreamCaptureValues.Int(payload, "content_index"),
                        partObject,
                        type == "response.content_part.done");
                }
                break;
        }
    }

    private ResponseOutputState? GetOutputState(int outputIndex)
    {
        if (!_outputStates.TryGetValue(outputIndex, out var state))
        {
            if (_outputStates.Count >= _budget.MaxCollectionItems)
            {
                _budget.MarkTruncated();
                return null;
            }

            state = new ResponseOutputState(_budget);
            _outputStates[outputIndex] = state;
        }

        return state;
    }

    private void CaptureResponseEnvelope(IReadOnlyDictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("response", out var response)
            || !StreamCaptureValues.TryObject(response, out var responseObject))
        {
            return;
        }

        _fallbackResponse = ProjectResponse(responseObject, includeOutput: false);
    }

    private Dictionary<string, object?> ProjectResponse(
        IReadOnlyDictionary<string, object?> response,
        bool includeOutput = true)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in LoggableResponseFields)
        {
            if (!includeOutput && field == "output")
            {
                continue;
            }

            if (!response.TryGetValue(field, out var value))
            {
                continue;
            }

            if (field is "output" or "error" or "incomplete_details"
                && !_budget.Fits(value))
            {
                continue;
            }

            result[field] = StreamCaptureValues.CloneValue(value);
        }

        return result;
    }

    private sealed class ResponseOutputState
    {
        private readonly StreamCaptureBudget _budget;
        private readonly SortedDictionary<int, StringBuilder> _outputText = [];
        private readonly SortedDictionary<int, StringBuilder> _refusals = [];
        private readonly SortedDictionary<int, StringBuilder> _reasoningSummary = [];
        private readonly SortedDictionary<int, Dictionary<string, object?>> _contentParts = [];
        private Dictionary<string, object?>? _baseItem;
        private Dictionary<string, object?>? _completedItem;
        private StringBuilder? _toolPayload;
        private string? _toolPayloadField;
        private string? _inferredType;

        public ResponseOutputState(StreamCaptureBudget budget)
        {
            _budget = budget;
        }

        public void CaptureAddedItem(IReadOnlyDictionary<string, object?> item)
        {
            _baseItem = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in BaseOutputItemFields)
            {
                if (item.TryGetValue(field, out var value))
                {
                    _baseItem[field] = CaptureScalar(value);
                }
            }

            var type = StreamCaptureValues.String(item, "type");
            if (type == "message")
            {
                _baseItem["content"] = new List<object?>();
            }
            else if (type == "reasoning")
            {
                _baseItem["summary"] = new List<object?>();
            }
            else if (type == "function_call")
            {
                _baseItem["arguments"] = string.Empty;
            }
            else if (type == "custom_tool_call")
            {
                _baseItem["input"] = string.Empty;
            }
        }

        public void CaptureCompletedItem(IReadOnlyDictionary<string, object?> item)
        {
            if (_budget.Fits(item))
            {
                _completedItem = StreamCaptureValues.CloneObject(item);
            }
        }

        public void AppendOutputText(int contentIndex, string? delta)
        {
            _inferredType ??= "message";
            _budget.Append(GetBuilder(_outputText, contentIndex), delta);
        }

        public void AppendReasoningSummary(int summaryIndex, string? delta)
        {
            _inferredType ??= "reasoning";
            _budget.Append(GetBuilder(_reasoningSummary, summaryIndex), delta);
        }

        public void CaptureOutputTextDone(int contentIndex, string? text)
        {
            var builder = GetBuilder(_outputText, contentIndex);
            if (builder.Length == 0)
            {
                AppendOutputText(contentIndex, text);
            }
        }

        public void CaptureReasoningSummaryDone(int summaryIndex, string? text)
        {
            var builder = GetBuilder(_reasoningSummary, summaryIndex);
            if (builder.Length == 0)
            {
                AppendReasoningSummary(summaryIndex, text);
            }
        }

        public void AppendRefusal(int contentIndex, string? delta)
        {
            _inferredType ??= "message";
            _budget.Append(GetBuilder(_refusals, contentIndex), delta);
        }

        public void CaptureRefusalDone(int contentIndex, string? refusal)
        {
            var builder = GetBuilder(_refusals, contentIndex);
            if (builder.Length == 0)
            {
                AppendRefusal(contentIndex, refusal);
            }
        }

        public void CaptureContentPart(
            int contentIndex,
            IReadOnlyDictionary<string, object?> part,
            bool completed)
        {
            if (_contentParts.Count >= _budget.MaxCollectionItems
                && !_contentParts.ContainsKey(contentIndex))
            {
                _budget.MarkTruncated();
                return;
            }

            if (completed && _budget.Fits(part))
            {
                _contentParts[contentIndex] = StreamCaptureValues.CloneObject(part);
                return;
            }

            var projected = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in BaseContentPartFields)
            {
                if (!part.TryGetValue(field, out var value))
                {
                    continue;
                }

                projected[field] = field == "type" ? value : CaptureScalar(value);
            }

            _contentParts[contentIndex] = projected;
        }

        public void AppendToolPayload(string type, string field, string? delta)
        {
            _inferredType ??= type;
            _toolPayloadField ??= field;
            _toolPayload ??= new StringBuilder();
            _budget.Append(_toolPayload, delta);
        }

        public void CaptureToolPayloadDone(string type, string field, string? value)
        {
            if (_toolPayload is null || _toolPayload.Length == 0)
            {
                AppendToolPayload(type, field, value);
            }
        }

        public Dictionary<string, object?> Build()
        {
            if (_completedItem is not null)
            {
                return StreamCaptureValues.CloneObject(_completedItem);
            }

            var item = _baseItem is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : StreamCaptureValues.CloneObject(_baseItem);
            var type = StreamCaptureValues.String(item, "type") ?? _inferredType;
            if (type is not null)
            {
                item["type"] = type;
            }

            if (type == "message" || _outputText.Count > 0)
            {
                item.TryAdd("role", "assistant");
                var contentIndexes = _contentParts.Keys
                    .Concat(_outputText.Keys)
                    .Concat(_refusals.Keys)
                    .Distinct()
                    .Order()
                    .Take(_budget.MaxCollectionItems);
                item["content"] = contentIndexes.Select(index => (object?)BuildContentPart(index)).ToList();
            }

            if (type == "reasoning" || _reasoningSummary.Count > 0)
            {
                item["summary"] = _reasoningSummary.Select(pair => (object?)new Dictionary<string, object?>
                {
                    ["type"] = "summary_text",
                    ["text"] = pair.Value.ToString()
                }).ToList();
            }

            if (_toolPayload is not null && _toolPayloadField is not null)
            {
                item[_toolPayloadField] = _toolPayload.ToString();
            }

            return item;
        }

        private Dictionary<string, object?> BuildContentPart(int index)
        {
            if (_contentParts.TryGetValue(index, out var capturedPart))
            {
                var part = StreamCaptureValues.CloneObject(capturedPart);
                if (_outputText.TryGetValue(index, out var outputText))
                {
                    part["type"] = "output_text";
                    part["text"] = outputText.ToString();
                }
                else if (_refusals.TryGetValue(index, out var refusal))
                {
                    part["type"] = "refusal";
                    part["refusal"] = refusal.ToString();
                }

                return part;
            }

            if (_refusals.TryGetValue(index, out var refusalOnly))
            {
                return new Dictionary<string, object?>
                {
                    ["type"] = "refusal",
                    ["refusal"] = refusalOnly.ToString()
                };
            }

            return new Dictionary<string, object?>
            {
                ["type"] = "output_text",
                ["text"] = _outputText.TryGetValue(index, out var text) ? text.ToString() : string.Empty,
                ["annotations"] = new List<object?>()
            };
        }

        private object? CaptureScalar(object? value)
        {
            if (value is not string text || text.Length <= 4096)
            {
                return StreamCaptureValues.CloneValue(value);
            }

            _budget.MarkTruncated();
            return text[..4096];
        }

        private static StringBuilder GetBuilder(
            IDictionary<int, StringBuilder> builders,
            int index)
        {
            if (!builders.TryGetValue(index, out var builder))
            {
                builder = new StringBuilder();
                builders[index] = builder;
            }

            return builder;
        }
    }
}

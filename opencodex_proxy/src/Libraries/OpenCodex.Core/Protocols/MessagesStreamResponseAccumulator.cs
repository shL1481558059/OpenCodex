using System.Text;
using System.Text.Json;

namespace OpenCodex.Core.Protocols;

internal sealed class MessagesStreamResponseAccumulator : IStreamResponseAccumulator
{
    private static readonly HashSet<string> AllowedMessageFields = new(StringComparer.Ordinal)
    {
        "id", "type", "role", "model", "stop_reason", "stop_sequence"
    };

    private static readonly HashSet<string> AllowedUsageFields = new(StringComparer.Ordinal)
    {
        "input_tokens",
        "output_tokens",
        "cache_creation_input_tokens",
        "cache_read_input_tokens",
        "cache_creation",
        "server_tool_use"
    };

    private readonly StreamCaptureBudget _budget;
    private readonly SortedDictionary<int, ContentBlockCapture> _contentBlocks = [];
    private readonly Dictionary<string, object?> _message = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _usage = new(StringComparer.Ordinal);
    private Dictionary<string, object?>? _errorResponse;
    private bool _hasMessage;

    public MessagesStreamResponseAccumulator(StreamCaptureBudget budget)
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
        switch (type)
        {
            case "message_start":
                CaptureMessageStart(payload);
                break;
            case "content_block_start":
                CaptureContentBlockStart(payload);
                break;
            case "content_block_delta":
                CaptureContentBlockDelta(payload);
                break;
            case "content_block_stop":
                break;
            case "message_delta":
                CaptureMessageDelta(payload);
                break;
            case "message_stop":
                IsComplete = true;
                break;
            case "error":
                CaptureError(payload);
                IsComplete = true;
                break;
        }
    }

    public Dictionary<string, object?>? BuildResponse()
    {
        if (_errorResponse is not null)
        {
            return StreamCaptureValues.CloneObject(_errorResponse);
        }

        if (!_hasMessage && _contentBlocks.Count == 0)
        {
            return null;
        }

        var response = StreamCaptureValues.CloneObject(_message);
        response.TryAdd("type", "message");
        response.TryAdd("role", "assistant");
        response["content"] = _contentBlocks.Values
            .Select(block => (object?)block.Build())
            .ToList();

        if (_usage.Count > 0)
        {
            response["usage"] = StreamCaptureValues.CloneObject(_usage);
        }

        return response;
    }

    private void CaptureMessageStart(IReadOnlyDictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("message", out var messageValue)
            || !StreamCaptureValues.TryObject(messageValue, out var message))
        {
            return;
        }

        _hasMessage = true;
        foreach (var (key, value) in message)
        {
            if (!AllowedMessageFields.Contains(key))
            {
                continue;
            }

            CaptureMessageField(key, value);
        }

        if (message.TryGetValue("usage", out var usageValue)
            && StreamCaptureValues.TryObject(usageValue, out var usage))
        {
            MergeUsage(usage);
        }

        if (!message.TryGetValue("content", out var contentValue)
            || !StreamCaptureValues.TryList(contentValue, out var content))
        {
            return;
        }

        for (var index = 0; index < content.Count; index++)
        {
            if (_contentBlocks.Count >= _budget.MaxCollectionItems)
            {
                _budget.MarkTruncated();
                break;
            }

            if (StreamCaptureValues.TryObject(content[index], out var contentBlock))
            {
                _contentBlocks[index] = new ContentBlockCapture(contentBlock, _budget);
            }
        }
    }

    private void CaptureContentBlockStart(IReadOnlyDictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("content_block", out var contentBlockValue)
            || !StreamCaptureValues.TryObject(contentBlockValue, out var contentBlock))
        {
            return;
        }

        var index = StreamCaptureValues.Int(payload, "index", _contentBlocks.Count);
        if (!_contentBlocks.ContainsKey(index)
            && _contentBlocks.Count >= _budget.MaxCollectionItems)
        {
            _budget.MarkTruncated();
            return;
        }

        _contentBlocks[index] = new ContentBlockCapture(contentBlock, _budget);
    }

    private void CaptureContentBlockDelta(IReadOnlyDictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("delta", out var deltaValue)
            || !StreamCaptureValues.TryObject(deltaValue, out var delta))
        {
            return;
        }

        var index = StreamCaptureValues.Int(payload, "index", _contentBlocks.Count);
        if (!_contentBlocks.TryGetValue(index, out var contentBlock))
        {
            if (_contentBlocks.Count >= _budget.MaxCollectionItems)
            {
                _budget.MarkTruncated();
                return;
            }

            contentBlock = new ContentBlockCapture(InferContentBlock(delta), _budget);
            _contentBlocks[index] = contentBlock;
        }

        contentBlock.AcceptDelta(delta);
    }

    private void CaptureMessageDelta(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.TryGetValue("delta", out var deltaValue)
            && StreamCaptureValues.TryObject(deltaValue, out var delta))
        {
            _hasMessage = true;
            foreach (var (key, value) in delta)
            {
                if (key is "stop_reason" or "stop_sequence")
                {
                    CaptureMessageField(key, value);
                }
            }
        }

        if (payload.TryGetValue("usage", out var usageValue)
            && StreamCaptureValues.TryObject(usageValue, out var usage))
        {
            MergeUsage(usage);
        }
    }

    private void CaptureError(IReadOnlyDictionary<string, object?> payload)
    {
        var response = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "error"
        };

        if (payload.TryGetValue("request_id", out var requestId))
        {
            if (requestId is string requestIdText && requestIdText.Length > 4096)
            {
                _budget.MarkTruncated();
                response["request_id"] = requestIdText[..4096];
            }
            else
            {
                response["request_id"] = StreamCaptureValues.CloneValue(requestId);
            }
        }

        if (payload.TryGetValue("error", out var errorValue)
            && StreamCaptureValues.TryObject(errorValue, out var error))
        {
            var capturedError = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in error)
            {
                if (key == "message" && value is string message)
                {
                    var capturedMessage = new StringBuilder();
                    _budget.Append(capturedMessage, message);
                    capturedError[key] = capturedMessage.ToString();
                }
                else
                {
                    if (_budget.Fits(value))
                    {
                        capturedError[key] = StreamCaptureValues.CloneValue(value);
                    }
                }
            }

            response["error"] = capturedError;
        }

        _errorResponse = response;
    }

    private static Dictionary<string, object?> InferContentBlock(
        IReadOnlyDictionary<string, object?> delta)
    {
        var deltaType = StreamCaptureValues.String(delta, "type");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = deltaType switch
            {
                "thinking_delta" or "signature_delta" => "thinking",
                "input_json_delta" => "tool_use",
                _ => "text"
            }
        };
    }

    private void MergeUsage(IReadOnlyDictionary<string, object?> usage)
    {
        foreach (var (key, value) in usage)
        {
            if (!AllowedUsageFields.Contains(key))
            {
                continue;
            }

            if (value is int or long or double or decimal)
            {
                _usage[key] = value;
                continue;
            }

            if (_budget.Fits(value))
            {
                _usage[key] = StreamCaptureValues.CloneValue(value);
            }
        }
    }

    private void CaptureMessageField(string key, object? value)
    {
        if (value is string text && text.Length > 4096)
        {
            _budget.MarkTruncated();
            _message[key] = text[..4096];
            return;
        }

        _message[key] = StreamCaptureValues.CloneValue(value);
    }

    private sealed class ContentBlockCapture
    {
        private readonly StreamCaptureBudget _budget;
        private readonly Dictionary<string, object?> _fields = new(StringComparer.Ordinal);
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _thinking = new();
        private readonly StringBuilder _signature = new();
        private readonly StringBuilder _inputJson = new();
        private readonly List<object?> _citations = [];
        private object? _initialInput;
        private bool _hasText;
        private bool _hasThinking;
        private bool _hasSignature;
        private bool _hasInputJsonDelta;

        public ContentBlockCapture(
            IReadOnlyDictionary<string, object?> contentBlock,
            StreamCaptureBudget budget)
        {
            _budget = budget;
            foreach (var (key, value) in contentBlock)
            {
                switch (key)
                {
                    case "text":
                        _hasText = true;
                        _budget.Append(_text, value as string);
                        break;
                    case "thinking":
                        _hasThinking = true;
                        _budget.Append(_thinking, value as string);
                        break;
                    case "signature":
                        _hasSignature = true;
                        _budget.Append(_signature, value as string);
                        break;
                    case "input":
                        if (_budget.Fits(value))
                        {
                            _initialInput = StreamCaptureValues.CloneValue(value);
                        }
                        break;
                    default:
                        CaptureField(key, value);
                        break;
                }
            }
        }

        public void AcceptDelta(IReadOnlyDictionary<string, object?> delta)
        {
            var deltaType = StreamCaptureValues.String(delta, "type");
            switch (deltaType)
            {
                case "text_delta":
                    _hasText = true;
                    _budget.Append(_text, StreamCaptureValues.String(delta, "text"));
                    break;
                case "thinking_delta":
                    _hasThinking = true;
                    _budget.Append(_thinking, StreamCaptureValues.String(delta, "thinking"));
                    break;
                case "signature_delta":
                    _hasSignature = true;
                    _budget.Append(_signature, StreamCaptureValues.String(delta, "signature"));
                    break;
                case "input_json_delta":
                    _hasInputJsonDelta = true;
                    _budget.Append(_inputJson, StreamCaptureValues.String(delta, "partial_json"));
                    break;
                case "citations_delta":
                case "citation_delta":
                    if (delta.TryGetValue("citation", out var citation)
                        && _citations.Count < _budget.MaxCollectionItems
                        && _budget.Fits(citation))
                    {
                        _citations.Add(StreamCaptureValues.CloneValue(citation));
                    }
                    else if (_citations.Count >= _budget.MaxCollectionItems)
                    {
                        _budget.MarkTruncated();
                    }
                    break;
            }
        }

        public Dictionary<string, object?> Build()
        {
            var contentBlock = StreamCaptureValues.CloneObject(_fields);
            if (_hasText)
            {
                contentBlock["text"] = _text.ToString();
            }

            if (_hasThinking)
            {
                contentBlock["thinking"] = _thinking.ToString();
            }

            if (_hasSignature)
            {
                contentBlock["signature"] = _signature.ToString();
            }

            if (_hasInputJsonDelta)
            {
                contentBlock["input"] = ParseInputJson() ?? _initialInput
                    ?? new Dictionary<string, object?>();
            }
            else if (_initialInput is not null)
            {
                contentBlock["input"] = StreamCaptureValues.CloneValue(_initialInput);
            }

            if (_citations.Count > 0)
            {
                contentBlock["citations"] = _citations.Select(StreamCaptureValues.CloneValue).ToList();
            }

            return contentBlock;
        }

        private void CaptureField(string key, object? value)
        {
            if (key is "type" or "id" or "name")
            {
                if (value is string structuralText && structuralText.Length > 4096)
                {
                    _budget.MarkTruncated();
                    _fields[key] = structuralText[..4096];
                }
                else
                {
                    _fields[key] = StreamCaptureValues.CloneValue(value);
                }
                return;
            }

            if (value is string text)
            {
                var captured = new StringBuilder();
                _budget.Append(captured, text);
                _fields[key] = captured.ToString();
                return;
            }

            if (_budget.Fits(value))
            {
                _fields[key] = StreamCaptureValues.CloneValue(value);
            }
        }

        private object? ParseInputJson()
        {
            if (_inputJson.Length == 0)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(_inputJson.ToString());
                return StreamCaptureValues.FromJsonElement(document.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

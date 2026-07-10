using System.Text;
using System.Text.Json;

namespace OpenCodex.Core.Protocols;

internal enum StreamCaptureTermination
{
    Completed,
    UpstreamError,
    ClientCancelled,
    UnexpectedEnd
}

internal sealed record StreamResponseCaptureResult(
    Dictionary<string, object?>? Response,
    bool Completed,
    bool Truncated,
    int MalformedEventCount);

internal interface IStreamResponseAccumulator
{
    bool IsComplete { get; }

    void Accept(SseEvent streamEvent);

    Dictionary<string, object?>? BuildResponse();
}

internal sealed class StreamResponseCapture
{
    internal const int DefaultMaxCapturedBytes = 1024 * 1024;
    internal const int MaxCapturedCollectionItems = 256;
    private const int MaxPendingSseDataBytes = 256 * 1024;
    private const int MaxPendingSseDataLines = 1024;

    private readonly IStreamResponseAccumulator _accumulator;
    private readonly StreamCaptureBudget _budget;
    private readonly List<string> _pendingDataLines = [];
    private string _eventName = "message";
    private int _malformedEventCount;
    private int _pendingDataBytes;
    private bool _discardPendingUntilBoundary;
    private bool _observerFailed;
    private StreamResponseCaptureResult? _completedResult;

    public StreamResponseCapture(
        string protocol,
        int maxCapturedBytes = DefaultMaxCapturedBytes)
    {
        _budget = new StreamCaptureBudget(maxCapturedBytes);
        _accumulator = protocol switch
        {
            ProtocolConverter.Responses => new ResponsesStreamResponseAccumulator(_budget),
            ProtocolConverter.Chat => new ChatStreamResponseAccumulator(_budget),
            ProtocolConverter.Messages => new MessagesStreamResponseAccumulator(_budget),
            _ => new UsageOnlyStreamResponseAccumulator()
        };
    }

    public void Accept(string chunk)
    {
        if (_observerFailed)
        {
            return;
        }

        try
        {
            if (chunk.Length == 0)
            {
                AcceptLine(string.Empty);
                return;
            }

            var normalized = chunk.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var parts = normalized.Split('\n');
            var count = normalized.EndsWith('\n') ? parts.Length - 1 : parts.Length;
            for (var index = 0; index < count; index++)
            {
                AcceptLine(parts[index]);
            }
        }
        catch (Exception exception) when (IsRecoverableCaptureException(exception))
        {
            _observerFailed = true;
        }
    }

    public StreamResponseCaptureResult Complete(StreamCaptureTermination termination)
    {
        if (_completedResult is not null)
        {
            return _completedResult;
        }

        FlushPendingData(markMalformed: true);
        Dictionary<string, object?>? response;
        try
        {
            response = _accumulator.BuildResponse();
        }
        catch (Exception exception) when (IsRecoverableCaptureException(exception))
        {
            _observerFailed = true;
            response = null;
        }
        var completed = termination == StreamCaptureTermination.Completed
            && _accumulator.IsComplete
            && !_observerFailed;

        var needsCaptureMetadata = !completed
            || _budget.Truncated
            || _malformedEventCount > 0
            || _observerFailed;
        if (response is null && needsCaptureMetadata)
        {
            response = new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        if (response is not null && needsCaptureMetadata)
        {
            response["_opencodex_capture"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["completed"] = completed,
                ["termination"] = termination.ToString(),
                ["truncated"] = _budget.Truncated,
                ["malformed_events"] = _malformedEventCount,
                ["observer_failed"] = _observerFailed
            };
        }

        _completedResult = new StreamResponseCaptureResult(
            response,
            completed,
            _budget.Truncated,
            _malformedEventCount);
        return _completedResult;
    }

    private void AcceptLine(string line)
    {
        if (line.Length == 0)
        {
            if (_discardPendingUntilBoundary)
            {
                _discardPendingUntilBoundary = false;
                ClearPendingData();
                _eventName = "message";
                return;
            }

            FlushPendingData(markMalformed: true);
            _eventName = "message";
            return;
        }

        if (line.StartsWith(':'))
        {
            return;
        }

        if (_discardPendingUntilBoundary)
        {
            return;
        }

        if (line.StartsWith("event:", StringComparison.Ordinal))
        {
            _eventName = line["event:".Length..].Trim();
            return;
        }

        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return;
        }

        var data = line["data:".Length..].TrimStart();
        if (data == "[DONE]")
        {
            FlushPendingData(markMalformed: true);
            _accumulator.Accept(new SseEvent(_eventName, data));
            return;
        }

        var dataBytes = Encoding.UTF8.GetByteCount(data);
        if (_pendingDataLines.Count >= MaxPendingSseDataLines
            || _pendingDataBytes + dataBytes + 1 > MaxPendingSseDataBytes)
        {
            _budget.MarkTruncated();
            _malformedEventCount++;
            _discardPendingUntilBoundary = true;
            ClearPendingData();
            return;
        }

        if (_pendingDataLines.Count > 0)
        {
            var pending = string.Join("\n", _pendingDataLines);
            var combined = $"{pending}\n{data}";
            if (CanParseData(combined))
            {
                _pendingDataLines.Add(data);
                _pendingDataBytes += dataBytes + 1;
                return;
            }

            if (CanParseData(pending))
            {
                TryAcceptData(pending);
                ClearPendingData();
            }
            else if (CanParseData(data))
            {
                _malformedEventCount++;
                ClearPendingData();
            }
        }

        _pendingDataLines.Add(data);
        _pendingDataBytes += dataBytes + 1;
    }

    private void FlushPendingData(bool markMalformed)
    {
        if (_pendingDataLines.Count == 0)
        {
            return;
        }

        var data = string.Join("\n", _pendingDataLines);
        if (TryAcceptData(data))
        {
            _pendingDataLines.Clear();
            _pendingDataBytes = 0;
            return;
        }

        if (!markMalformed)
        {
            return;
        }

        _malformedEventCount++;
        ClearPendingData();
    }

    private bool TryAcceptData(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            _accumulator.Accept(new SseEvent(
                _eventName,
                StreamCaptureValues.FromJsonElement(document.RootElement)));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool CanParseData(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsRecoverableCaptureException(Exception exception)
    {
        return exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;
    }

    private void ClearPendingData()
    {
        _pendingDataLines.Clear();
        _pendingDataBytes = 0;
    }
}

internal sealed class StreamCaptureBudget
{
    private int _remainingBytes;

    public StreamCaptureBudget(
        int maxBytes,
        int maxCollectionItems = StreamResponseCapture.MaxCapturedCollectionItems)
    {
        _remainingBytes = Math.Max(0, maxBytes);
        MaxCollectionItems = Math.Max(0, maxCollectionItems);
    }

    public bool Truncated { get; private set; }

    public int MaxCollectionItems { get; }

    public void MarkTruncated()
    {
        Truncated = true;
    }

    public void Append(StringBuilder target, string? fragment)
    {
        if (string.IsNullOrEmpty(fragment))
        {
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(fragment);
        if (byteCount <= _remainingBytes)
        {
            target.Append(fragment);
            _remainingBytes -= byteCount;
            return;
        }

        var low = 0;
        var high = fragment.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (Encoding.UTF8.GetByteCount(fragment.AsSpan(0, middle)) <= _remainingBytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low > 0)
        {
            if (low < fragment.Length
                && char.IsHighSurrogate(fragment[low - 1])
                && char.IsLowSurrogate(fragment[low]))
            {
                low--;
            }

            target.Append(fragment.AsSpan(0, low));
            _remainingBytes -= Encoding.UTF8.GetByteCount(fragment.AsSpan(0, low));
        }

        Truncated = true;
    }

    public bool Fits(object? value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value).Length;
        if (bytes <= _remainingBytes)
        {
            _remainingBytes -= bytes;
            return true;
        }

        Truncated = true;
        return false;
    }
}

internal static class StreamCaptureValues
{
    public static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => FromJsonElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer)
                ? integer is >= int.MinValue and <= int.MaxValue ? (int)integer : integer
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public static string? String(IReadOnlyDictionary<string, object?> value, string key)
    {
        return value.TryGetValue(key, out var item) ? item as string : null;
    }

    public static int Int(IReadOnlyDictionary<string, object?> value, string key, int fallback = 0)
    {
        return value.TryGetValue(key, out var item) ? ToInt(item, fallback) : fallback;
    }

    public static int ToInt(object? value, int fallback = 0)
    {
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            double doubleValue => Convert.ToInt32(doubleValue),
            decimal decimalValue => Convert.ToInt32(decimalValue),
            _ => fallback
        };
    }

    public static bool TryObject(object? value, out Dictionary<string, object?> result)
    {
        if (value is Dictionary<string, object?> dictionary)
        {
            result = dictionary;
            return true;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            result = readOnly.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return true;
        }

        result = [];
        return false;
    }

    public static bool TryList(object? value, out List<object?> result)
    {
        if (value is List<object?> list)
        {
            result = list;
            return true;
        }

        if (value is IEnumerable<object?> enumerable)
        {
            result = enumerable.ToList();
            return true;
        }

        result = [];
        return false;
    }

    public static Dictionary<string, object?> CloneObject(IReadOnlyDictionary<string, object?> value)
    {
        return value.ToDictionary(
            pair => pair.Key,
            pair => CloneValue(pair.Value),
            StringComparer.Ordinal);
    }

    public static object? CloneValue(object? value)
    {
        return value switch
        {
            IReadOnlyDictionary<string, object?> dictionary => CloneObject(dictionary),
            IEnumerable<object?> list when value is not string => list.Select(CloneValue).ToList(),
            _ => value
        };
    }
}

internal sealed class UsageOnlyStreamResponseAccumulator : IStreamResponseAccumulator
{
    private object? _model;
    private Dictionary<string, object?>? _usage;

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

        Capture(payload);
        if (payload.TryGetValue("response", out var response)
            && StreamCaptureValues.TryObject(response, out var responseObject))
        {
            Capture(responseObject);
        }

        if (payload.TryGetValue("message", out var message)
            && StreamCaptureValues.TryObject(message, out var messageObject))
        {
            Capture(messageObject);
        }
    }

    public Dictionary<string, object?>? BuildResponse()
    {
        return _model is null && _usage is null
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = _model,
                ["usage"] = _usage ?? new Dictionary<string, object?>()
            };
    }

    private void Capture(IReadOnlyDictionary<string, object?> value)
    {
        _model ??= StreamCaptureValues.String(value, "model");
        if (value.TryGetValue("usage", out var usage)
            && StreamCaptureValues.TryObject(usage, out var usageObject))
        {
            _usage = StreamCaptureValues.CloneObject(usageObject);
        }
    }
}

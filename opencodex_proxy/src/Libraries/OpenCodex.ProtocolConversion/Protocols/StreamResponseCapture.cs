using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using OpenCodex.ProtocolConversion.Abstractions;
using OpenCodex.ProtocolConversion.Streaming;
using OpenCodex.ProtocolConversion.Errors;

namespace OpenCodex.ProtocolConversion;

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

internal sealed class StreamResponseCapture : IDisposable
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
    private readonly ReceivedFrameEvidence? _evidence;
    private ExceptionDispatchInfo? _strictFailure, _completionFailure;
    private ReceivedFrameEvidenceSnapshot? _failureEvidence;
    private long _acceptedFrames;

    public StreamResponseCapture(
        string protocol,
        int maxCapturedBytes = DefaultMaxCapturedBytes)
        : this(protocol,new StreamCaptureBudget(maxCapturedBytes)) { }

    internal StreamResponseCapture(string protocol,StreamCaptureBudget budget,IReadOnlySet<string>? toolNames=null, int? maxEventBytes=null)
    {
        _budget=budget;
        _evidence = budget.IsStrict
            ? new ReceivedFrameEvidence(protocol, budget.MaxBytes,
                maxEventBytes ?? ProtocolConversionParameters.Current.SseMaxEventBytes, budget.Spool!, budget.CancellationToken)
            : null;
        _accumulator = protocol switch
        {
            ProtocolConverter.Responses => new ResponsesStreamResponseAccumulator(_budget),
            ProtocolConverter.Chat => new ChatStreamResponseAccumulator(_budget,toolNames),
            ProtocolConverter.Messages => new MessagesStreamResponseAccumulator(_budget),
            _ => new UsageOnlyStreamResponseAccumulator()
        };
    }

    internal IStreamResponseAccumulator Accumulator=>_accumulator;
    internal ConverterText CreateConverterText()=>new(_budget);
    internal CapturedTextSlice? BorrowText(object owner,string field)=>_budget.BorrowText(owner,field);
    internal ConverterFragments CreateFragments()=>new(_budget);
    internal ConverterArguments CreateArguments()=>new(_budget);
    public void Dispose()
    {
        try { _evidence?.Dispose(); }
        finally { _budget.Dispose(); }
        // GetFailureEvidence transfers its independent reader to the audit caller.
    }

    internal bool NeedsFailureEvidence => _strictFailure is not null || _completionFailure is not null
        || _budget.SourceFailure is not null
        || _evidence is { } evidence && evidence.RecordCount > _acceptedFrames;
    internal bool IsComplete => !NeedsFailureEvidence && !_observerFailed && _accumulator.IsComplete;

    /// <summary>Call before an upstream primer; Accept(frame, true) must consume the same ordered frames.</summary>
    internal void RecordReceivedFrame(SseFrame frame)
    {
        if (_evidence is null) return;
        _strictFailure?.Throw();
        try { _evidence.Append(frame); }
        catch (Exception exception) { _strictFailure ??= ExceptionDispatchInfo.Capture(exception); throw; }
    }

    /// <summary>The caller owns this snapshot until complete failure-audit persistence has finished.</summary>
    internal ReceivedFrameEvidenceSnapshot GetFailureEvidence(
        StreamCaptureTermination termination = StreamCaptureTermination.UpstreamError) =>
        _failureEvidence ??= (_evidence ?? throw new InvalidOperationException(
            "Received-frame evidence needs the explicitly injected strict capture store.")).Snapshot(termination);

    public void Accept(SseFrame frame, bool evidenceAlreadyRecorded = false)
    {
        if (_evidence is not null)
        {
            _strictFailure?.Throw();
            _completionFailure?.Throw();
            _budget.SourceFailure?.Throw();
            try
            {
                if (!evidenceAlreadyRecorded) RecordReceivedFrame(frame);
                else if (_evidence.RecordCount <= _acceptedFrames)
                    throw new InvalidOperationException("The ordered frame must be recorded before acceptance.");
                if (frame.Event is not null)
                {
                    if (frame.Malformed)
                    {
                        _malformedEventCount++;
                        throw new UpstreamException("The upstream audit contains malformed JSON; full received-frame evidence is required.");
                    }
                    _accumulator.Accept(frame.Event);
                }
                _acceptedFrames = checked(_acceptedFrames + 1);
                return;
            }
            catch (Exception exception) { _strictFailure ??= ExceptionDispatchInfo.Capture(exception); throw; }
        }
        if (_observerFailed || frame.Event is null) return;
        if (frame.Malformed) { _malformedEventCount++; if(_budget.IsStrict)throw new UpstreamException("The upstream audit contains malformed JSON; complete audit cannot be guaranteed.");return; }
        try { _accumulator.Accept(frame.Event); }
        catch (Exception exception) when (!_budget.IsStrict && IsRecoverableCaptureException(exception)) { _observerFailed = true; }
    }

    public void Accept(string chunk)
    {
        if (_evidence is not null)
        {
            _strictFailure?.Throw();
            _completionFailure?.Throw();
            _budget.SourceFailure?.Throw();
            try { _evidence.AppendChunk(chunk); }
            catch (Exception exception) { _strictFailure ??= ExceptionDispatchInfo.Capture(exception); throw; }
        }
        if (_observerFailed)
        {
            return;
        }

        try
        {
            if (chunk.Length == 0)
            {
                AcceptLine(string.Empty);
                if (_evidence is not null) _acceptedFrames = checked(_acceptedFrames + 1);
                return;
            }

            var normalized = chunk.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var parts = normalized.Split('\n');
            var count = normalized.EndsWith('\n') ? parts.Length - 1 : parts.Length;
            for (var index = 0; index < count; index++)
            {
                AcceptLine(parts[index]);
            }
            if (_evidence is not null) _acceptedFrames = checked(_acceptedFrames + 1);
        }
        catch (Exception exception) when (_budget.IsStrict && IsRecoverableCaptureException(exception))
        {
            _strictFailure ??= ExceptionDispatchInfo.Capture(exception);
            throw;
        }
        catch (Exception exception) when (!_budget.IsStrict && IsRecoverableCaptureException(exception))
        {
            _observerFailed = true;
        }
    }

    public StreamResponseCaptureResult Complete(StreamCaptureTermination termination)
    {
        if (_completedResult is not null) return _completedResult;
        _completionFailure?.Throw();
        _strictFailure?.Throw();
        _budget.SourceFailure?.Throw();
        try
        {
            if (_budget.IsStrict && NeedsFailureEvidence)
                throw new UpstreamException("The received stream includes unapplied frames; full received-frame evidence is required.");
            FlushPendingData(markMalformed: true);
            Dictionary<string, object?>? response;
            try { response = _accumulator.BuildResponse(); }
            catch (Exception exception) when (!_budget.IsStrict && IsRecoverableCaptureException(exception))
            {
                _observerFailed = true;
                response = null;
            }
            var completed = termination == StreamCaptureTermination.Completed
                && _accumulator.IsComplete && !_observerFailed;
            var needsCaptureMetadata = !completed || _budget.Truncated
                || _malformedEventCount > 0 || _observerFailed;
            if (response is null && needsCaptureMetadata)
                response = new Dictionary<string, object?>(StringComparer.Ordinal);
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
            // Cache the built source before the caller validates its normal C limit.
            // A failed build is separately cached below; never rebuild it in catch/finally.
            return _completedResult = new StreamResponseCaptureResult(response, completed,
                _budget.Truncated, _malformedEventCount);
        }
        catch (Exception exception) when (_budget.IsStrict && IsRecoverableCaptureException(exception))
        {
            _completionFailure ??= ExceptionDispatchInfo.Capture(exception);
            throw;
        }
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
            if (_budget.IsStrict)
                throw new UpstreamException("The upstream audit contains malformed JSON; full received-frame evidence is required.");
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

internal sealed class StreamCaptureBudget : IDisposable
{
    private int _remainingBytes;
    internal int MaxBytes { get; }
    internal ExceptionDispatchInfo? SourceFailure { get; private set; }
    internal void RecordSourceFailure(Exception exception) => SourceFailure ??= ExceptionDispatchInfo.Capture(exception);
    private readonly IProtocolAuditCapture? _spool;
    private readonly CancellationToken _cancellation;
    private readonly int _maxFields;
    private readonly List<CaptureText> _texts=[];
    private readonly List<CaptureBinary> _binaries=[];
    public bool IsStrict => _spool is not null;
    internal IProtocolAuditCapture? Spool => _spool;
    internal CancellationToken CancellationToken => _cancellation;
    internal int MemoryThresholdBytes => _spool?.MemoryThresholdBytes ?? int.MaxValue;
    internal StreamCaptureBudget(int maxBytes,int maxItems,int maxFields,IProtocolAuditCapture spool,CancellationToken cancellationToken)
        :this(maxBytes,maxItems)
    { if(maxBytes<=0 || maxItems<=0 || maxFields<=0)throw new ArgumentOutOfRangeException(nameof(maxBytes));_spool=spool;_cancellation=cancellationToken;_maxFields=maxFields; }
    internal CaptureText CreateText()
    {
        if(IsStrict && _texts.Count>=_maxFields)throw new UpstreamException("Complete stream audit exceeds its field budget; no content was truncated.", "output_capture_limit", 507);
        var text=new CaptureText(_spool,_cancellation);_texts.Add(text);return text;
    }
    internal CaptureBinary CreateBinary()
    {
        if(IsStrict && _binaries.Count>=_maxFields)throw new UpstreamException("Complete stream audit exceeds its field budget; no content was truncated.", "output_capture_limit", 507);
        var binary=new CaptureBinary(this);_binaries.Add(binary);return binary;
    }
    public void Dispose() { foreach(var binary in _binaries)binary.Dispose();_binaries.Clear();foreach(var text in _texts)text.Dispose();_texts.Clear();_eventText.Clear(); }
    // One event-scoped lookup: keys are parsed OBJECT IDENTITY + semantic field, not
    // equal delta text. It is cleared before the next Accept; no history/delta cache.
    private readonly Dictionary<CaptureField,CapturedTextSlice?> _eventText = new(new CaptureFieldComparer());
    internal void BeginEvent()=>_eventText.Clear();
    internal void AppendTracked(CaptureText target,object? owner,string field,string? fragment)
    {
        var offset=target.Length;Append(target,fragment);
        if(owner is null || string.IsNullOrEmpty(fragment) || target.Length-offset!=fragment.Length)return;
        var key=new CaptureField(owner,field);
        if(!_eventText.TryAdd(key,new(target,offset,fragment.Length)))_eventText[key]=null;
    }
    internal CapturedTextSlice? BorrowText(object owner,string field)=>_eventText.GetValueOrDefault(new(owner,field));
    private readonly record struct CaptureField(object Owner,string Field);
    private sealed class CaptureFieldComparer:IEqualityComparer<CaptureField>
    {
        public bool Equals(CaptureField left,CaptureField right)=>ReferenceEquals(left.Owner,right.Owner)&&StringComparer.Ordinal.Equals(left.Field,right.Field);
        public int GetHashCode(CaptureField value)=>HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Owner),StringComparer.Ordinal.GetHashCode(value.Field));
    }
    private void Consume(int bytes)
    {
        _cancellation.ThrowIfCancellationRequested();
        if(bytes>_remainingBytes)throw new UpstreamException("Complete stream audit exceeds its byte budget; no content was truncated.", "output_capture_limit", 507);
        _remainingBytes-=bytes;
    }
    public void Append(CaptureText target,string? fragment)
        => Append(target, fragment.AsSpan());
    internal void Append(CaptureText target,ReadOnlySpan<char> fragment)
    {
        if(fragment.IsEmpty)return;
        if(IsStrict) { Consume(Encoding.UTF8.GetByteCount(fragment));target.Append(fragment);return; }
        target.AppendLegacy(this,fragment);
    }
    internal void Spend(long bytes)
    {
        if (bytes <= 0) return;
        _cancellation.ThrowIfCancellationRequested();
        if (bytes > _remainingBytes)
            throw new UpstreamException("Complete stream audit exceeds its byte budget; no content was truncated.", "output_capture_limit", 507);
        _remainingBytes -= checked((int)bytes);
    }

    public StreamCaptureBudget(
        int maxBytes,
        int maxCollectionItems = StreamResponseCapture.MaxCapturedCollectionItems)
    {
        MaxBytes = Math.Max(0, maxBytes);
        _remainingBytes = MaxBytes;
        MaxCollectionItems = Math.Max(0, maxCollectionItems);
    }

    public bool Truncated { get; private set; }

    public int MaxCollectionItems { get; }

    public void MarkTruncated()
    {
        if(IsStrict)throw new UpstreamException("Complete stream audit exceeds its collection budget; no content was truncated.", "output_capture_limit", 507);
        Truncated = true;
    }

    public void Append(StringBuilder target, string? fragment)
        => Append(target, fragment.AsSpan());
    internal void Append(StringBuilder target, ReadOnlySpan<char> fragment)
    {
        if (fragment.IsEmpty)
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
            if (Encoding.UTF8.GetByteCount(fragment[..middle]) <= _remainingBytes)
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

            target.Append(fragment[..low]);
            _remainingBytes -= Encoding.UTF8.GetByteCount(fragment[..low]);
        }

        Truncated = true;
    }

    public bool Fits(object? value)
    {
        var bytes = checked((int)Json.ProtocolJsonWriter.Count(value, IsStrict, _cancellation));
        if(IsStrict)
        {
            Consume(bytes);return true;
        }
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

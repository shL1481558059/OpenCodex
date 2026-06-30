using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyStreamService : IProxyStreamService
{
    private static readonly JsonSerializerOptions StreamLogJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> LoggableResponseEventTypes = new(StringComparer.Ordinal)
    {
        "response.completed",
        "response.content_part.added",
        "response.content_part.done",
        "response.custom_tool_call_input.delta",
        "response.custom_tool_call_input.done",
        "response.error",
        "response.function_call_arguments.delta",
        "response.function_call_arguments.done",
        "response.output_item.added",
        "response.output_item.done",
        "response.output_text.delta",
        "response.output_text.done",
        "response.reasoning_summary_part.added",
        "response.reasoning_summary_part.done",
        "response.reasoning_summary_text.delta",
        "response.reasoning_summary_text.done"
    };

    private static readonly HashSet<string> LoggableUpstreamEventTypes = new(StringComparer.Ordinal)
    {
        "content_block_delta",
        "content_block_start",
        "content_block_stop",
        "error",
        "message_delta",
        "message_start",
        "message_stop"
    };

    private static readonly string[] ResponseCompletedAllowedFields =
    [
        "id",
        "object",
        "created_at",
        "completed_at",
        "status",
        "model",
        "usage",
        "error",
        "incomplete_details"
    ];

    private readonly IUpstreamClient _upstream;
    private readonly IProxyLogService _logs;
    private readonly IWebSearchSimulator _webSearch;

    public ProxyStreamService(
        IUpstreamClient upstream,
        IProxyLogService logs,
        IWebSearchSimulator webSearch)
    {
        _upstream = upstream;
        _logs = logs;
        _webSearch = webSearch;
    }

    public async Task StreamAsync(ProxyStreamContext context)
    {
        context.UpstreamRequest["stream"] = true;

        var ttftStarted = Stopwatch.GetTimestamp();
        StreamWriteMetrics? streamWriteMetrics = null;
        var ttftMs = (int?)null;
        var error = (string?)null;
        object? errorResponse = null;
        Dictionary<string, object?>? webSearchDetails = null;
        Dictionary<string, object?>? upstreamResponse = null;
        Dictionary<string, object?>? responsePayload = null;
        var streamLineCaptures = new List<ProxyRequestStreamLineCapture>();
        var statusCode = ProxyHttpStatus.Ok;
        var upstreamRequest = context.UpstreamRequest;
        var isConversion = context.EntryProtocol != context.ChannelType;
        Console.Error.WriteLine($"[OCXP-DEBUG] [{context.RequestId}] StreamAsync start: entry={context.EntryProtocol}, channel={context.ChannelType}, isConversion={isConversion}, model={VisibleModel(context)}, upstream={context.UpstreamModel}");

        try
        {
            if (_webSearch.CanSimulate(
                context.EntryProtocol,
                context.ChannelType,
                context.OwnerRole,
                context.Payload))
            {
                var streamResult = new WebSearchStreamResult();
                var visibleModel = VisibleModel(context);
                var streamLines = _webSearch.RunChatStreamAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.Payload,
                    visibleModel,
                    context.DefaultTimeout,
                    streamResult,
                    (lines, source) => CaptureLoggableStreamLines(
                        lines,
                        streamLineCaptures,
                        source,
                        context.CancellationToken),
                    context.CancellationToken);
                streamWriteMetrics = await context.StreamWriter.WriteLinesAsync(
                    CaptureLoggableStreamLines(
                        streamLines,
                        streamLineCaptures,
                        "downstream",
                        context.CancellationToken),
                    SseStreamConverter.CountsForTtft,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);
                ttftMs = streamWriteMetrics.TtftMs;

                upstreamRequest = streamResult.FinalUpstreamRequest ?? upstreamRequest;
                upstreamResponse = streamResult.FinalUpstreamResponse;
                responsePayload = streamResult.ResponsePayload;
                webSearchDetails = streamResult.Details;
            }
            else if (context.EntryProtocol == context.ChannelType)
            {
                Console.Error.WriteLine($"[OCXP-DEBUG] [{context.RequestId}] StreamAsync: PASSTHROUGH path (entry==channel)");
                var streamLines = _upstream.StreamJsonAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.DefaultTimeout,
                    context.CancellationToken);
                var capture = new PassThroughCapture();
                streamWriteMetrics = await context.StreamWriter.WriteLinesAsync(
                    CaptureStreamUsage(
                        CaptureLoggableStreamLines(
                            streamLines,
                            streamLineCaptures,
                            "upstream",
                            context.CancellationToken),
                        capture,
                        context.CancellationToken),
                    static line => line.Trim().Length > 0,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);
                Console.Error.WriteLine($"[OCXP-DEBUG] [{context.RequestId}] StreamAsync: PASSTHROUGH done. ttft={streamWriteMetrics.TtftMs}ms, first_sse={streamWriteMetrics.FirstSseEventMs}ms, completed={streamWriteMetrics.CompletedEventMs}ms");
                ttftMs = streamWriteMetrics.TtftMs;
                upstreamResponse = capture.UpstreamResponse;
            }
            else
            {
                Console.Error.WriteLine($"[OCXP-DEBUG] [{context.RequestId}] StreamAsync: CONVERSION path, creating IAsyncEnumerables...");
                var converted = new ConvertedStreamResult
                {
                    TextFormat = ProtocolConverter.ExtractTextFormat(context.OriginalPayload)
                };
                var visibleModel = VisibleModel(context);
                var streamLines = _upstream.StreamJsonAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.DefaultTimeout,
                    context.CancellationToken);
                var capturedStreamLines = CaptureLoggableStreamLines(
                    streamLines,
                    streamLineCaptures,
                    "upstream",
                    context.CancellationToken);
                // 方案A: 直接调用内部重载，消除外层 await foreach 包装
                var convertedLines = context.ChannelType == ProtocolConverter.Chat
                    ? SseStreamConverter.ChatToResponsesEvents(
                        capturedStreamLines,
                        visibleModel,
                        converted,
                        SkipToolNames: null,
                        SkipResponseCreated: false,
                        InitialSequenceNumber: 0,
                        InitialOutputIndex: 0,
                        context.CancellationToken)
                    : SseStreamConverter.MessagesToResponsesEvents(
                        capturedStreamLines,
                        visibleModel,
                        converted,
                        SkipToolNames: null,
                        SkipResponseCreated: false,
                        InitialSequenceNumber: 0,
                        InitialOutputIndex: 0,
                        context.CancellationToken);
                Console.Error.WriteLine($"[OCXP-DEBUG] [{context.RequestId}] StreamAsync: CONVERSION enumerables created, starting WriteLinesAsync loop...");
                var writeLoopStart = Stopwatch.GetTimestamp();
                streamWriteMetrics = await context.StreamWriter.WriteLinesAsync(
                    CaptureLoggableStreamLines(
                        convertedLines,
                        streamLineCaptures,
                        "downstream",
                        context.CancellationToken),
                    SseStreamConverter.CountsForTtft,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);
                Console.Error.WriteLine($"[OCXP-DEBUG] [{context.RequestId}] StreamAsync: CONVERSION done. ttft={streamWriteMetrics.TtftMs}ms, first_sse={streamWriteMetrics.FirstSseEventMs}ms, first_output_text={streamWriteMetrics.FirstOutputTextDeltaMs}ms, first_reasoning={streamWriteMetrics.FirstReasoningSummaryTextDeltaMs}ms, completed={streamWriteMetrics.CompletedEventMs}ms");
                ttftMs = streamWriteMetrics.TtftMs;

                upstreamResponse = converted.UpstreamResponse;
                responsePayload = upstreamResponse is null
                    ? null
                    : ProtocolConverter.ConvertResponse(
                        upstreamResponse,
                        context.EntryProtocol,
                        context.ChannelType,
                        context.Route.OriginalModel,
                        converted.TextFormat);
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            if (exception is ProxyException proxyException)
            {
                statusCode = proxyException.StatusCode;
                errorResponse = proxyException.ToResponse();
                upstreamResponse = UpstreamErrorBody(proxyException) ?? upstreamResponse;
            }
            throw;
        }
        finally
        {
            _logs.CompleteLog(
                context.RequestLogId,
                new ProxyLogContext(
                    context.RequestId,
                    context.OwnerUsername,
                    context.ApiKeyId,
                    context.OriginalPayload,
                    upstreamRequest,
                    upstreamResponse,
                    responsePayload,
                    errorResponse,
                    context.RequestModel,
                    context.UpstreamModel,
                    context.ChannelId,
                    context.ChannelType,
                    IsStream: true,
                    TtftMs: ttftMs,
                    StatusCode: statusCode,
                    DurationMs: ElapsedMilliseconds(context.StartedTimestamp),
                    error,
                    webSearchDetails,
                    StreamWriteMetrics: streamWriteMetrics,
                    StreamLines: streamLineCaptures),
                context.RequestMetadata);
        }
    }

    private static string? VisibleModel(ProxyStreamContext context)
    {
        return context.Route.OriginalModel.Length > 0
            ? context.Route.OriginalModel
            : context.RequestModel;
    }

    private static int ElapsedMilliseconds(long started)
    {
        return (int)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MidpointRounding.AwayFromZero);
    }

    private static Dictionary<string, object?>? UpstreamErrorBody(ProxyException exception)
    {
        if (exception is UpstreamException { Body: not null } upstream)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["error"] = upstream.Body
            };
        }

        return null;
    }

    internal sealed class PassThroughCapture
    {
        public Dictionary<string, object?>? UpstreamResponse { get; set; }
    }

    internal static async IAsyncEnumerable<string> CaptureStreamUsage(
        IAsyncEnumerable<string> lines,
        PassThroughCapture capture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        object? model = null;
        object? usage = null;
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            yield return line;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            var json = line["data:".Length..].TrimStart();
            if (json.Length == 0 || json == "[DONE]")
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                model ??= TryExtractString(root, "model");
                usage ??= TryExtractObject(root, "usage");
                if (root.TryGetProperty("response", out var responseEl)
                    && responseEl.ValueKind == JsonValueKind.Object)
                {
                    model ??= TryExtractString(responseEl, "model");
                    usage ??= TryExtractObject(responseEl, "usage");
                }
                if (root.TryGetProperty("message", out var messageEl)
                    && messageEl.ValueKind == JsonValueKind.Object)
                {
                    model ??= TryExtractString(messageEl, "model");
                    usage ??= TryExtractObject(messageEl, "usage");
                }
            }
            catch (JsonException)
            {
            }
        }
        capture.UpstreamResponse = model is null && usage is null
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = model,
                ["usage"] = usage ?? new Dictionary<string, object?>()
            };
    }

    internal static IAsyncEnumerable<string> CaptureRawStreamLines(
        IAsyncEnumerable<string> lines,
        IList<ProxyRequestStreamLineCapture> capture,
        CancellationToken cancellationToken)
    {
        return CaptureLoggableStreamLines(lines, capture, "upstream", cancellationToken);
    }

    internal static async IAsyncEnumerable<string> CaptureLoggableStreamLines(
        IAsyncEnumerable<string> lines,
        IList<ProxyRequestStreamLineCapture> capture,
        string source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = new StreamLogCaptureState();
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            CaptureLoggableStreamChunk(line, capture, source, state);
            yield return line;
        }
    }

    private static void CaptureLoggableStreamChunk(
        string chunk,
        IList<ProxyRequestStreamLineCapture> capture,
        string source,
        StreamLogCaptureState state)
    {
        foreach (var rawLine in SplitStreamLogLines(chunk))
        {
            if (rawLine.Length == 0)
            {
                if (state.HasOpenLoggedEvent)
                {
                    AddStreamLineCapture(capture, source, string.Empty);
                }

                state.HasOpenLoggedEvent = false;
                state.CurrentEventName = null;
                continue;
            }

            if (TryBuildLoggableStreamLine(rawLine, state, out var logLine))
            {
                AddStreamLineCapture(capture, source, logLine);
                state.HasOpenLoggedEvent = true;
            }
        }
    }

    private static IEnumerable<string> SplitStreamLogLines(string chunk)
    {
        var normalized = chunk.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var parts = normalized.Split('\n');
        var count = normalized.EndsWith('\n') ? parts.Length - 1 : parts.Length;
        for (var i = 0; i < count; i++)
        {
            yield return parts[i];
        }
    }

    private static bool TryBuildLoggableStreamLine(
        string rawLine,
        StreamLogCaptureState state,
        out string logLine)
    {
        logLine = string.Empty;
        if (rawLine.StartsWith("event:", StringComparison.Ordinal))
        {
            var eventName = rawLine["event:".Length..].Trim();
            state.CurrentEventName = eventName;
            if (!IsLoggableEventName(eventName))
            {
                return false;
            }

            logLine = rawLine;
            return true;
        }

        if (!rawLine.StartsWith("data:", StringComparison.Ordinal))
        {
            return false;
        }

        var data = rawLine["data:".Length..].TrimStart();
        if (data.Length == 0)
        {
            return false;
        }

        if (data == "[DONE]")
        {
            logLine = rawLine;
            return true;
        }

        return TryBuildLoggableDataLine(data, state.CurrentEventName, rawLine, out logLine);
    }

    private static bool TryBuildLoggableDataLine(
        string data,
        string? currentEventName,
        string rawLine,
        out string logLine)
    {
        logLine = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return IsLoggableEventName(currentEventName, includeCompleted: false)
                    && UseRawLine(rawLine, out logLine);
            }

            var root = document.RootElement;
            var type = TryExtractString(root, "type") ?? currentEventName;
            if (string.Equals(type, "response.created", StringComparison.Ordinal)
                || string.Equals(type, "response.in_progress", StringComparison.Ordinal)
                || string.Equals(type, "response.metadata", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(type, "response.completed", StringComparison.Ordinal))
            {
                return TryBuildResponseCompletedSummary(data, out logLine);
            }

            if (IsLoggableEventName(type, includeCompleted: false)
                || HasChatCompletionDelta(root)
                || HasChatCompletionUsage(root)
                || HasMessagesStreamPayload(root))
            {
                return UseRawLine(rawLine, out logLine);
            }

            return false;
        }
        catch (JsonException)
        {
            return IsLoggableEventName(currentEventName, includeCompleted: false)
                && UseRawLine(rawLine, out logLine);
        }
    }

    private static bool TryBuildResponseCompletedSummary(string data, out string logLine)
    {
        logLine = string.Empty;
        try
        {
            var node = JsonNode.Parse(data) as JsonObject;
            if (node is null)
            {
                return false;
            }

            if (node["response"] is JsonObject response)
            {
                var cleaned = new JsonObject();
                foreach (var field in ResponseCompletedAllowedFields)
                {
                    if (response.TryGetPropertyValue(field, out var value))
                    {
                        cleaned[field] = value?.DeepClone();
                    }
                }

                node["response"] = cleaned;
            }

            logLine = $"data: {node.ToJsonString(StreamLogJsonOptions)}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasChatCompletionDelta(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object
                && delta.EnumerateObject().Any())
            {
                return true;
            }

            if (choice.TryGetProperty("finish_reason", out var finishReason)
                && finishReason.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasChatCompletionUsage(JsonElement root)
    {
        return root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object;
    }

    private static bool HasMessagesStreamPayload(JsonElement root)
    {
        var type = TryExtractString(root, "type");
        return type is not null && LoggableUpstreamEventTypes.Contains(type);
    }

    private static bool IsLoggableEventName(string? eventName, bool includeCompleted = true)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return false;
        }

        return LoggableUpstreamEventTypes.Contains(eventName)
            || (includeCompleted
                ? LoggableResponseEventTypes.Contains(eventName)
                : LoggableResponseEventTypes.Contains(eventName)
                    && !string.Equals(eventName, "response.completed", StringComparison.Ordinal));
    }

    private static bool UseRawLine(string rawLine, out string logLine)
    {
        logLine = rawLine;
        return true;
    }

    private static void AddStreamLineCapture(
        IList<ProxyRequestStreamLineCapture> capture,
        string source,
        string rawLine)
    {
        capture.Add(new ProxyRequestStreamLineCapture(
            capture.Count,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            source,
            rawLine));
    }

    internal static string? TryExtractString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } str
            ? str
            : null;
    }
    internal static object? TryExtractObject(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return FromJsonElement(value);
    }
    internal static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                p => p.Name,
                p => FromJsonElement(p.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l)
                ? (l is >= int.MinValue and <= int.MaxValue ? (int)l : l)
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
    private sealed class StreamLogCaptureState
    {
        public string? CurrentEventName { get; set; }

        public bool HasOpenLoggedEvent { get; set; }
    }
}

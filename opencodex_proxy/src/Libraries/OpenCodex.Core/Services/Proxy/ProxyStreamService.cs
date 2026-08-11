using System.Diagnostics;
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
        StreamResponseCapture? passThroughResponseCapture = null;
        var passThroughTermination = StreamCaptureTermination.UnexpectedEnd;
        var streamLineCaptures = new List<ProxyRequestStreamLineCapture>();
        var statusCode = ProxyHttpStatus.Ok;
        var upstreamRequest = context.UpstreamRequest;
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
                    (lines, source) => CaptureStreamLines(
                        lines,
                        streamLineCaptures,
                        source,
                        context.CancellationToken),
                    context.CancellationToken);
                streamWriteMetrics = await context.StreamWriter.WriteLinesAsync(
                    EnsureCompletedStreamEndsWithDone(
                        CaptureStreamLines(
                            streamLines,
                            streamLineCaptures,
                            "downstream",
                            context.CancellationToken),
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
                var streamLines = _upstream.StreamJsonAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.DefaultTimeout,
                    context.CancellationToken);
                passThroughResponseCapture = new StreamResponseCapture(context.ChannelType);
                streamWriteMetrics = await context.StreamWriter.WriteLinesAsync(
                    EnsureCompletedStreamEndsWithDone(
                        CapturePassThroughResponse(
                            CaptureStreamLines(
                                streamLines,
                                streamLineCaptures,
                                "upstream",
                                context.CancellationToken),
                            passThroughResponseCapture,
                            context.CancellationToken),
                        streamLineCaptures,
                        "downstream",
                        context.CancellationToken),
                    static line => line.Trim().Length > 0,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);
                ttftMs = streamWriteMetrics.TtftMs;
                passThroughTermination = StreamCaptureTermination.Completed;
                upstreamResponse = passThroughResponseCapture
                    .Complete(passThroughTermination)
                    .Response;
            }
            else
            {
                var converted = new ConvertedStreamResult
                {
                    TextFormat = ProtocolConverter.ExtractTextFormat(context.OriginalPayload),
                    ToolCallMappings = context.EntryProtocol == ProtocolConverter.Responses
                        && (context.ChannelType == ProtocolConverter.Chat
                            || context.ChannelType == ProtocolConverter.Messages)
                        ? ProtocolConverter.BuildResponsesToolCallMappings(context.Payload)
                        : null
                };
                var visibleModel = VisibleModel(context);
                var streamLines = _upstream.StreamJsonAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.DefaultTimeout,
                    context.CancellationToken);
                var capturedStreamLines = CaptureStreamLines(
                    streamLines,
                    streamLineCaptures,
                    "upstream",
                    context.CancellationToken);
                var confirmedStreamLines = await ConfirmUpstreamStreamStartedAsync(
                    capturedStreamLines,
                    context.CancellationToken);
                // 按 (入口协议, 上游协议) 派发到对应流式转换器；下游事件格式取决于入口协议。
                IAsyncEnumerable<string> convertedLines;
                var includeChatUsage = context.Payload.TryGetValue("stream_options", out var streamOptionsValue)
                    && streamOptionsValue is Dictionary<string, object?> streamOptions
                    && streamOptions.TryGetValue("include_usage", out var includeUsageValue)
                    && includeUsageValue is true;
                switch ((context.EntryProtocol, context.ChannelType))
                {
                    case (ProtocolConverter.Responses, ProtocolConverter.Chat):
                        convertedLines = SseStreamConverter.ChatToResponsesEvents(
                            confirmedStreamLines,
                            visibleModel,
                            converted,
                            SkipToolNames: null,
                            SkipResponseCreated: false,
                            InitialSequenceNumber: 0,
                            InitialOutputIndex: 0,
                            context.CancellationToken);
                        break;
                    case (ProtocolConverter.Responses, ProtocolConverter.Messages):
                        convertedLines = SseStreamConverter.MessagesToResponsesEvents(
                            confirmedStreamLines,
                            visibleModel,
                            converted,
                            SkipToolNames: null,
                            SkipResponseCreated: false,
                            InitialSequenceNumber: 0,
                            InitialOutputIndex: 0,
                            context.CancellationToken);
                        break;
                    case (ProtocolConverter.Messages, ProtocolConverter.Chat):
                        convertedLines = SseStreamConverter.ChatToMessagesEvents(
                            confirmedStreamLines,
                            visibleModel,
                            converted,
                            SkipToolNames: null,
                            SkipMessageStart: false,
                            context.CancellationToken);
                        break;
                    case (ProtocolConverter.Chat, ProtocolConverter.Messages):
                        convertedLines = SseStreamConverter.MessagesToChatEvents(
                            confirmedStreamLines,
                            visibleModel,
                            converted,
                            SkipToolNames: null,
                            IncludeUsage: includeChatUsage,
                            context.CancellationToken);
                        break;
                    case (ProtocolConverter.Chat, ProtocolConverter.Responses):
                        convertedLines = SseStreamConverter.ResponsesToChatEvents(
                            confirmedStreamLines,
                            visibleModel,
                            converted,
                            SkipToolNames: null,
                            context.CancellationToken);
                        break;
                    case (ProtocolConverter.Messages, ProtocolConverter.Responses):
                        convertedLines = SseStreamConverter.ResponsesToMessagesEvents(
                            confirmedStreamLines,
                            visibleModel,
                            converted,
                            SkipToolNames: null,
                            SkipMessageStart: false,
                            context.CancellationToken);
                        break;
                    default:
                        // 理论上不可达：SupportsStreamingConversion 已在上游拦截未实现方向。
                        throw new BadRequestException(
                            $"streaming conversion not implemented for {context.EntryProtocol} to {context.ChannelType}");
                }
                streamWriteMetrics = await context.StreamWriter.WriteLinesAsync(
                    EnsureCompletedStreamEndsWithDone(
                        CaptureStreamLines(
                            convertedLines,
                            streamLineCaptures,
                            "downstream",
                            context.CancellationToken),
                        streamLineCaptures,
                        "downstream",
                        context.CancellationToken),
                    SseStreamConverter.CountsForTtft,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);
                ttftMs = streamWriteMetrics.TtftMs;

                upstreamResponse = converted.UpstreamResponse;
                responsePayload = upstreamResponse is null
                    ? null
                    : ProtocolConverter.ConvertResponse(
                        upstreamResponse,
                        context.EntryProtocol,
                        context.ChannelType,
                        context.Route.OriginalModel,
                        converted.TextFormat,
                        converted.ToolCallMappings);
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            passThroughTermination = exception is OperationCanceledException
                ? StreamCaptureTermination.ClientCancelled
                : StreamCaptureTermination.UpstreamError;
            var capturedUpstreamResponse = passThroughResponseCapture?
                .Complete(passThroughTermination)
                .Response;
            if (exception is ProxyException proxyException)
            {
                statusCode = proxyException.StatusCode;
                errorResponse = proxyException.ToResponse();
                upstreamResponse = CombineCapturedAndErrorResponse(
                    capturedUpstreamResponse,
                    UpstreamErrorBody(proxyException))
                    ?? upstreamResponse;
            }
            else
            {
                upstreamResponse ??= capturedUpstreamResponse;
            }
            throw;
        }
        finally
        {
            if (passThroughResponseCapture is not null && upstreamResponse is null)
            {
                upstreamResponse = passThroughResponseCapture
                    .Complete(passThroughTermination)
                    .Response;
            }

            await _logs.CompleteLogAsync(
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

    private static async Task<IAsyncEnumerable<string>> ConfirmUpstreamStreamStartedAsync(
        IAsyncEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        var enumerator = lines.GetAsyncEnumerator(cancellationToken);
        bool hasFirstLine;
        try
        {
            hasFirstLine = await enumerator.MoveNextAsync();
        }
        catch
        {
            await enumerator.DisposeAsync();
            throw;
        }

        if (!hasFirstLine)
        {
            await enumerator.DisposeAsync();
            return EmptyStreamLines(cancellationToken);
        }

        return ReplayPrimedStreamLines(
            enumerator.Current,
            enumerator,
            cancellationToken);
    }

    private static async IAsyncEnumerable<string> EmptyStreamLines(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<string> ReplayPrimedStreamLines(
        string firstLine,
        IAsyncEnumerator<string> enumerator,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return firstLine;

            while (await enumerator.MoveNextAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
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

    private static Dictionary<string, object?>? CombineCapturedAndErrorResponse(
        Dictionary<string, object?>? captured,
        Dictionary<string, object?>? errorResponse)
    {
        if (captured is null)
        {
            return errorResponse;
        }

        if (errorResponse is null)
        {
            return captured;
        }

        var hasProtocolResponse = captured.Keys.Any(key => key != "_opencodex_capture");
        if (!hasProtocolResponse)
        {
            if (captured.TryGetValue("_opencodex_capture", out var captureMetadata))
            {
                errorResponse["_opencodex_capture"] = captureMetadata;
            }

            return errorResponse;
        }

        foreach (var (key, value) in errorResponse)
        {
            captured.TryAdd(key, value);
        }

        return captured;
    }

    internal static async IAsyncEnumerable<string> CapturePassThroughResponse(
        IAsyncEnumerable<string> lines,
        StreamResponseCapture capture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            capture.Accept(line);
            yield return line;
            if (capture.IsComplete)
            {
                yield break;
            }
        }
    }

    internal static async IAsyncEnumerable<string> CaptureStreamLines(
        IAsyncEnumerable<string> lines,
        IList<ProxyRequestStreamLineCapture> capture,
        string source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            foreach (var rawLine in SplitStreamLogLines(line))
            {
                AddStreamLineCapture(capture, source, rawLine);
            }

            yield return line;
        }
    }

    private static async IAsyncEnumerable<string> EnsureCompletedStreamEndsWithDone(
        IAsyncEnumerable<string> lines,
        IList<ProxyRequestStreamLineCapture> capture,
        string source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sawCompleted = false;
        var sawDone = false;
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            sawCompleted |= line.Contains("response.completed", StringComparison.Ordinal);
            sawDone |= line.Contains("data: [DONE]", StringComparison.Ordinal);
            yield return line;
        }

        if (!sawCompleted || sawDone)
        {
            yield break;
        }

        const string done = "data: [DONE]\n\n";
        foreach (var rawLine in SplitStreamLogLines(done))
        {
            AddStreamLineCapture(capture, source, rawLine);
        }

        yield return done;
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


    private static void AddStreamLineCapture(
        IList<ProxyRequestStreamLineCapture> capture,
        string source,
        string rawLine)
    {
        capture.Add(new ProxyRequestStreamLineCapture(
            capture.Count,
            source,
            rawLine));
    }

}

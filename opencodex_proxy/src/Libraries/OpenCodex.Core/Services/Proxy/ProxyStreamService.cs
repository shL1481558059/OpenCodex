using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;

namespace OpenCodex.Core.Services.Proxy;

public sealed partial class ProxyStreamService : IProxyStreamService
{
    private readonly IUpstreamClient _upstream;
    private readonly IProxyLogService _logs;
    private readonly IWebSearchToolExecutor _webSearch;
    private readonly WebSearchContinuationStore _webSearchHistory;
    private readonly ResponsesUpstreamTransport? _responsesWebSocket;
    private readonly SseFrameOptions _outputFrames;
    private readonly ProxyAuditSpool _auditSpool;

    public ProxyStreamService(
        IUpstreamClient upstream,
        IProxyLogService logs,
        IWebSearchToolExecutor webSearch,
        WebSearchContinuationStore webSearchHistory,
        ResponsesUpstreamTransport? responsesWebSocket = null,
        SseFrameOptions? outputFrames = null, ProxyAuditSpool? auditSpool = null)
    {
        _upstream = upstream;
        _logs = logs;
        _webSearch = webSearch;
        _webSearchHistory = webSearchHistory;
        _responsesWebSocket = responsesWebSocket;
        _outputFrames = outputFrames ?? new();
        _auditSpool=auditSpool ?? ProxyAuditSpool.Default;
        if (_outputFrames.MaxCaptureBytes <=0 || _outputFrames.MaxCaptureItems<=0 || _outputFrames.MaxCaptureFields<=0 || _outputFrames.MaxEventBytes <= 0 || _outputFrames.MaxBufferedRoundBytes <= 0 || _outputFrames.MaxBufferedRoundFrames <= 0) throw new ArgumentOutOfRangeException(nameof(outputFrames));
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
        using var passThroughResponseCapture=context.EntryProtocol==context.ChannelType
            ? CreateFullCapture(context.ChannelType,context.CancellationToken) : null;
        var streamTermination = StreamCaptureTermination.UnexpectedEnd;
        var streamLogCapture = new StreamLogCapture(context.EntryProtocol);
        var statusCode = ProxyHttpStatus.Ok;
        var terminalGate = new StreamTerminalGate();
        var upstreamRequest = context.UpstreamRequest;
        ConvertedProxyStreamState? convertedState = null;
        BuiltinToolSession? tools = null;
        try
        {
            using var toolLifetime = tools = context.BuiltinTools is null ? null : new BuiltinToolSession(
                context.BuiltinTools, _webSearch, _webSearchHistory,
                context.ChannelType, context.DefaultTimeout, BuiltinToolSession.OutputTokenBudget(context.Payload));
            if (context.EntryProtocol == context.ChannelType)
            {
                var streamFrames = _responsesWebSocket is not null
                    && context.ChannelType == ProtocolConverter.Responses
                    ? _responsesWebSocket.StreamEventsAsync(context, context.CancellationToken)
                    : _upstream.StreamEventsAsync(context.Route.Channel, upstreamRequest,
                        context.DefaultTimeout, context.CancellationToken);
                streamWriteMetrics = await context.StreamWriter.WriteFrameBatchAsync(
                    terminalGate.Hold(ObserveFrames(streamFrames, streamLogCapture, passThroughResponseCapture,
                        context.CancellationToken),context.CancellationToken),
                    static line => line.Trim().Length > 0,
                    () => ElapsedMilliseconds(ttftStarted), context.CancellationToken);
                ttftMs = streamWriteMetrics.TtftMs;
                streamTermination = streamLogCapture.SawTerminalEvent
                    ? StreamCaptureTermination.Completed
                    : StreamCaptureTermination.UnexpectedEnd;
                upstreamResponse = passThroughResponseCapture!
                    .Complete(streamTermination)
                    .Response;
            }
            else
            {
                convertedState = new ConvertedProxyStreamState
                {
                    UpstreamRequest = upstreamRequest,
                    UpstreamRequestBody = context.RequestMetadata.LogBodies?.UpstreamRequest
                };
                var convertedLines = ConvertRoundsAsync(context, convertedState, tools, context.CancellationToken);
                streamWriteMetrics = await context.StreamWriter.WriteFrameBatchAsync(
                    terminalGate.Hold(ObserveFrames(ValidateOutputFrames(convertedLines, context.CancellationToken),
                        streamLogCapture, null, context.CancellationToken),context.CancellationToken),
                    SseStreamConverter.CountsForTtft,
                    () => ElapsedMilliseconds(ttftStarted), context.CancellationToken);
                ttftMs = streamWriteMetrics.TtftMs;
                streamTermination = streamLogCapture.SawTerminalEvent
                    ? StreamCaptureTermination.Completed
                    : StreamCaptureTermination.UnexpectedEnd;

                upstreamRequest = convertedState.UpstreamRequest;
                upstreamResponse = convertedState.UpstreamResponse;
                responsePayload = convertedState.ResponsePayload;
            }

            if (HasStreamFailure(upstreamResponse) || HasStreamFailure(responsePayload))
            {
                throw StreamFailure(HasStreamFailure(upstreamResponse) ? upstreamResponse! : responsePayload!);
            }
            if (streamTermination != StreamCaptureTermination.Completed
                || passThroughResponseCapture is not null && !passThroughResponseCapture.IsComplete)
            {
                throw new UpstreamException("upstream stream ended before its terminal event", body: upstreamResponse);
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            streamTermination = exception is OperationCanceledException && context.CancellationToken.IsCancellationRequested
                ? StreamCaptureTermination.ClientCancelled
                : StreamCaptureTermination.UpstreamError;
            var capturedUpstreamResponse = passThroughResponseCapture?
                .Complete(streamTermination)
                .Response;
            if (exception is ProxyException proxyException)
            {
                statusCode = proxyException.StatusCode;
                errorResponse = proxyException.ToResponse();
                upstreamResponse = UpstreamErrorPayload.Combine(
                    capturedUpstreamResponse ?? upstreamResponse ?? convertedState?.UpstreamResponse,
                    UpstreamErrorPayload.FromException(proxyException))
                    ?? upstreamResponse;
            }
            else
            {
                statusCode = exception is OperationCanceledException
                    ? context.CancellationToken.IsCancellationRequested
                        ? ProxyHttpStatus.ClientClosedRequest
                        : ProxyHttpStatus.GatewayTimeout
                    : ProxyHttpStatus.InternalServerError;
                upstreamResponse ??= capturedUpstreamResponse;
            }
            terminalGate.Fail(context.EntryProtocol,error,(exception as UpstreamException)?.ErrorCode);
            if (exception is OperationCanceledException && !context.CancellationToken.IsCancellationRequested)
            {
                var timeout = new UpstreamException("proxy request timed out", ProxyHttpStatus.GatewayTimeout);
                error = timeout.Message;
                errorResponse = timeout.ToResponse();
                throw timeout;
            }
            throw;
        }
        finally
        {
            if (streamTermination != StreamCaptureTermination.Completed || error is not null)
            {
                error = streamLogCapture.ComposeErrorText(error, streamTermination);
            }

            if (convertedState is not null)
            {
                upstreamRequest = convertedState.UpstreamRequest;
                upstreamResponse ??= convertedState.UpstreamResponse;
                responsePayload ??= convertedState.ResponsePayload;
            }
            webSearchDetails = tools?.Details;
            if (passThroughResponseCapture is not null && upstreamResponse is null)
            {
                upstreamResponse = passThroughResponseCapture!
                    .Complete(streamTermination)
                    .Response;
            }

            await PersistLog();
            if(error is not null && terminalGate.HasCommitted && !context.CancellationToken.IsCancellationRequested)
                await context.StreamWriter.WriteFramesAsync(terminalGate.Release(context.CancellationToken),static _=>false,
                    ()=>ElapsedMilliseconds(ttftStarted),context.CancellationToken);
        }
        // Success/stop is not publishable until complete audit persistence succeeds. This
        // does not delay any preceding role, tool-start or text event.
        try
        {
            await context.StreamWriter.WriteFramesAsync(terminalGate.Release(context.CancellationToken),
                static _=>false,()=>ElapsedMilliseconds(ttftStarted),context.CancellationToken);
        }
        catch(Exception exception)
        {
            statusCode=exception is OperationCanceledException?ProxyHttpStatus.ClientClosedRequest:ProxyHttpStatus.InternalServerError;
            error="Terminal stream write/flush failed: "+exception.Message;
            try { await PersistLog(); }
            catch(Exception auditError) { throw new AggregateException("Terminal output and failure audit both failed.",exception,auditError); }
            throw;
        }
        async Task PersistLog()
        {
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
                    webSearchDetails)
                {
                    UpstreamRequestBody = convertedState?.UpstreamRequestBody ?? context.RequestMetadata.LogBodies?.UpstreamRequest,
                    AggregatedUsage = tools?.HasAccountingUsage == true
                        ? tools.AccountingUsage
                        : convertedState?.RejectedUsage.Count > 0 ? convertedState.RejectedUsage : null
                },
                context.RequestMetadata);
        }
    }

    private StreamResponseCapture CreateFullCapture(string protocol,CancellationToken cancellationToken,IReadOnlySet<string>? toolNames=null) =>
        new(protocol,new StreamCaptureBudget(_outputFrames.MaxCaptureBytes,_outputFrames.MaxCaptureItems,
            _outputFrames.MaxCaptureFields,_auditSpool,cancellationToken),toolNames);

    private static bool HasStreamFailure(Dictionary<string, object?>? response)
    {
        return response is not null
            && (response.TryGetValue("error", out var error) && error is not null
                || response.TryGetValue("status", out var status) && status is "failed");
    }

    private static UpstreamException StreamFailure(Dictionary<string, object?> response)
    {
        var statusCode = ProxyHttpStatus.BadGateway;
        var message = "upstream stream failed";
        if (JsonDictionaryValue.Get(response, "error") is IReadOnlyDictionary<string, object?> upstreamError)
        {
            var code = JsonDictionaryValue.String(upstreamError, "code");
            var type = JsonDictionaryValue.String(upstreamError, "type");
            if (code is "rate_limit_exceeded" or "rate_limit_error"
                || type is "rate_limit_exceeded" or "rate_limit_error")
            {
                statusCode = ProxyHttpStatus.TooManyRequests;
            }

            var upstreamMessage = JsonDictionaryValue.String(upstreamError, "message");
            if (upstreamMessage.Length > 0)
            {
                message = upstreamMessage;
            }
            var identifier = code.Length > 0 ? code : type;
            if (identifier.Length > 0)
            {
                message = $"{message} ({identifier})";
            }
        }

        return new UpstreamException(message, statusCode, body: response);
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

    private async IAsyncEnumerable<SseFrame> ValidateOutputFrames(
        IAsyncEnumerable<SseFrame> frames, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.WithCancellation(cancellationToken))
        {
            if (frame.Utf8Length > _outputFrames.MaxEventBytes)
                throw new InvalidDataException("SSE event exceeds configured MaxEventBytes; output was not truncated.");
            yield return frame;
        }
    }

    private static async IAsyncEnumerable<SseFrame> ObserveFrames(
        IAsyncEnumerable<SseFrame> frames, StreamLogCapture log, StreamResponseCapture? capture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sawCompleted = false;
        var sawDone = false;
        await foreach (var frame in frames.WithCancellation(cancellationToken))
        {
            capture?.Accept(frame);
            log.Observe(frame);
            sawCompleted |= frame.IsResponseCompleted && frame.BoundaryComplete;
            sawDone |= frame.IsDone;
            yield return frame;
            if (capture?.IsComplete == true) break;
        }
        if (sawCompleted && !sawDone)
        {
            log.Observe(SseFrame.Done);
            yield return SseFrame.Done;
        }
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

    internal static async IAsyncEnumerable<string> CaptureStreamLogLines(
        IAsyncEnumerable<string> lines,
        StreamLogCapture capture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            foreach (var rawLine in SplitStreamLogLines(line))
            {
                capture.Observe(rawLine);
            }

            yield return line;
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
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCodex.Core.Errors;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Streaming;
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
        ProxyAuditSpool auditSpool,
        ResponsesUpstreamTransport? responsesWebSocket = null,
        SseFrameOptions? outputFrames = null)
    {
        _upstream = upstream;
        _logs = logs;
        _webSearch = webSearch;
        _webSearchHistory = webSearchHistory;
        _responsesWebSocket = responsesWebSocket;
        _outputFrames = outputFrames ?? new();
        ArgumentNullException.ThrowIfNull(auditSpool);
        _auditSpool = auditSpool;
        if (_outputFrames.MaxCaptureBytes <=0 || _outputFrames.MaxCaptureItems<=0 || _outputFrames.MaxCaptureFields<=0 || _outputFrames.MaxEventBytes <= 0 || _outputFrames.MaxBufferedRoundBytes <= 0 || _outputFrames.MaxBufferedRoundFrames <= 0) throw new ArgumentOutOfRangeException(nameof(outputFrames));
    }

    public async Task StreamAsync(ProxyStreamContext context)
    {
        using var executionBudget = RequestExecutionBudget.Create(_auditSpool, context.RequestMetadata,
            context.SnapshotReservation, CancellationToken.None);
        var requestMetadata = executionBudget.Attach(context.RequestMetadata);
        Exception? primaryFailure = null;
        var sourceCompleteAttempted = false;
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
            ? CreateFullCapture(context.ChannelType, executionBudget, context.CancellationToken) : null;
        var streamTermination = StreamCaptureTermination.UnexpectedEnd;
        var streamLogCapture = new StreamLogCapture(context.EntryProtocol);
        var statusCode = ProxyHttpStatus.Ok;
        var terminalGate = new StreamTerminalGate();
        var upstreamRequest = context.UpstreamRequest;
        ConvertedProxyStreamState? convertedState = null;
        BuiltinToolSession? tools = null;
        try
        {
            executionBudget.EnsureQueued(requestMetadata, context.OriginalPayload, context.CancellationToken);
            executionBudget.EnsureCall(requestMetadata, context.Payload, upstreamRequest, _outputFrames, context.ChannelType,
                isStream: true, context.CancellationToken, hasBuiltinTools: context.BuiltinTools is not null);
            var bodies = requestMetadata.LogBodies ?? new ProxyLogBodyHandles(null, null);
            requestMetadata = requestMetadata.WithLogBodies(bodies with
            {
                Headers = bodies.Headers ?? executionBudget.CaptureAuditJson(requestMetadata.Headers, context.CancellationToken),
                Request = bodies.Request ?? (requestMetadata.RawBody is { Length: > 0 } raw
                    ? executionBudget.CaptureAuditUtf8(raw.Span, context.CancellationToken)
                    : executionBudget.CaptureAuditJson(context.OriginalPayload, context.CancellationToken)),
                UpstreamRequest = bodies.UpstreamRequest ?? executionBudget.CaptureAuditJson(upstreamRequest, context.CancellationToken)
            });
            using var toolLifetime = tools = context.BuiltinTools is null ? null : new BuiltinToolSession(
                context.BuiltinTools, _webSearch, _webSearchHistory,
                context.ChannelType, context.DefaultTimeout, BuiltinToolSession.OutputTokenBudget(context.Payload))
            {
                BeforeToolExecution = (sourceRequest, details, token) => executionBudget.EnsureTool(
                    details, sourceRequest, _outputFrames, context.ChannelType, isStream: true, token),
                AfterToolExecution = RequestExecutionBudget.ValidateToolResult
            };
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
                sourceCompleteAttempted = true;
                upstreamResponse = CompleteCapture(passThroughResponseCapture!, streamTermination).Response;
                RequestExecutionBudget.ValidateCompleteJson(upstreamResponse, "source", _outputFrames.MaxCaptureBytes,
                    context.CancellationToken);
            }
            else
            {
                convertedState = new ConvertedProxyStreamState
                {
                    UpstreamRequest = upstreamRequest,
                    UpstreamRequestBody = requestMetadata.LogBodies?.UpstreamRequest
                };
                var convertedLines = ConvertRoundsAsync(context, convertedState, tools, executionBudget, requestMetadata, context.CancellationToken);
                streamWriteMetrics = await context.StreamWriter.WriteFrameBatchAsync(
                    terminalGate.Hold(ObserveFrames(ValidateOutputFrames(convertedLines, context.CancellationToken),
                        streamLogCapture, null, context.CancellationToken),context.CancellationToken),
                    SseStreamConverter.FrameCountsForTtft,
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
            var failure = ResourceErrors.Normalize(exception);
            primaryFailure = failure;
            error = failure.Message;
            streamTermination = exception is OperationCanceledException && context.CancellationToken.IsCancellationRequested
                ? StreamCaptureTermination.ClientCancelled
                : StreamCaptureTermination.UpstreamError;
            var evidenceResponse = convertedState?.FailureEvidence;
            if (convertedState?.ToolExecutionPhase != true
                && exception is UpstreamException { Body: Dictionary<string, object?> httpBody }
                && UpstreamJsonEvidence.IsEvidence(httpBody))
                evidenceResponse = httpBody;
            if (evidenceResponse is null && failure is StorageCapacityException)
                evidenceResponse = convertedState?.CurrentEvidence;
            if (passThroughResponseCapture is not null)
            {
                // A normal network stop may still have a trustworthy canonical prefix.
                // Complete once; rejected/malformed/over-C sources use exact evidence instead.
                if (evidenceResponse is null && !sourceCompleteAttempted && failure is not StorageCapacityException
                    && !passThroughResponseCapture.NeedsFailureEvidence)
                {
                    sourceCompleteAttempted = true;
                    try
                    {
                        upstreamResponse = CompleteCapture(passThroughResponseCapture, streamTermination).Response;
                        RequestExecutionBudget.ValidateCompleteJson(upstreamResponse, "source", _outputFrames.MaxCaptureBytes,
                            CancellationToken.None);
                    }
                    catch (Exception captureFailure)
                    {
                        if (ResourceErrors.IsResourceFailure(captureFailure))
                            failure = ResourceErrors.Combine(failure, captureFailure);
                        evidenceResponse = executionBudget.Retain(
                            passThroughResponseCapture.GetFailureEvidence(streamTermination)).Response;
                    }
                }
                if (evidenceResponse is null && (failure is StorageCapacityException
                    || passThroughResponseCapture.NeedsFailureEvidence))
                    evidenceResponse = executionBudget.Retain(
                        passThroughResponseCapture.GetFailureEvidence(streamTermination)).Response;
            }
            primaryFailure = failure;
            error = failure.Message;
            var capturedUpstreamResponse = upstreamResponse ?? convertedState?.UpstreamResponse;
            if (evidenceResponse is not null)
                upstreamResponse = evidenceResponse;
            else if (convertedState?.ToolExecutionPhase != true && failure is ProxyException proxySourceFailure
                && (proxySourceFailure is not UpstreamException upstreamFailure
                    || !ReferenceEquals(upstreamFailure.Body, capturedUpstreamResponse)))
                upstreamResponse = UpstreamErrorPayload.Combine(capturedUpstreamResponse,
                    UpstreamErrorPayload.FromException(proxySourceFailure)) ?? capturedUpstreamResponse;
            else
                upstreamResponse = capturedUpstreamResponse;
            if (failure is ProxyException proxyException)
            {
                statusCode = proxyException.StatusCode;
                errorResponse = proxyException.ToResponse();
            }
            else
            {
                statusCode = exception is OperationCanceledException
                    ? context.CancellationToken.IsCancellationRequested
                        ? ProxyHttpStatus.ClientClosedRequest
                        : ProxyHttpStatus.GatewayTimeout
                    : ProxyHttpStatus.InternalServerError;
            }
            terminalGate.Fail(context.EntryProtocol, error, failure is StorageCapacityException capacity
                ? capacity.ErrorCode : (failure as UpstreamException)?.ErrorCode,
                failure is ProxyException proxyFailure ? proxyFailure.StatusCode : null);
            if (exception is OperationCanceledException && !context.CancellationToken.IsCancellationRequested)
            {
                var timeout = new UpstreamException("proxy request timed out", ProxyHttpStatus.GatewayTimeout);
                error = timeout.Message;
                errorResponse = timeout.ToResponse();
                primaryFailure = timeout;
                throw timeout;
            }
            if (!ReferenceEquals(failure, exception)) throw failure;
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
            if (passThroughResponseCapture is not null && upstreamResponse is null && !sourceCompleteAttempted
                && primaryFailure is null)
            {
                sourceCompleteAttempted = true;
                upstreamResponse = CompleteCapture(passThroughResponseCapture, streamTermination).Response;
            }

            try { await PersistLog(); }
            catch (Exception auditFailure) when (primaryFailure is not null)
            {
                throw ResourceErrors.Combine(primaryFailure, auditFailure);
            }
            if (error is not null && terminalGate.HasCommitted && !context.CancellationToken.IsCancellationRequested)
            {
                try
                {
                    await context.StreamWriter.WriteFramesAsync(terminalGate.Release(context.CancellationToken), SseFramePredicate.Never,
                        () => ElapsedMilliseconds(ttftStarted), context.CancellationToken);
                }
                catch (Exception sendFailure) when (primaryFailure is not null)
                {
                    // Error delivery is secondary to the normalized operation failure.
                    // In particular a callback's generic upstream exception must not
                    // replace a local 507 or make its channel look unhealthy.
                    throw ResourceErrors.Combine(primaryFailure, ResourceErrors.Normalize(sendFailure));
                }
            }
        }
        // Success/stop is not publishable until complete audit persistence succeeds. This
        // does not delay any preceding role, tool-start or text event.
        try
        {
            await context.StreamWriter.WriteFramesAsync(terminalGate.Release(context.CancellationToken),
                SseFramePredicate.Never,()=>ElapsedMilliseconds(ttftStarted),context.CancellationToken);
        }
        catch(Exception exception)
        {
            var failure = ResourceErrors.Normalize(exception);
            statusCode = failure is ProxyException proxy ? proxy.StatusCode
                : exception is OperationCanceledException ? ProxyHttpStatus.ClientClosedRequest : ProxyHttpStatus.InternalServerError;
            error = "Terminal stream write/flush failed: " + failure.Message;
            if (failure is ProxyException terminalFailure) errorResponse = terminalFailure.ToResponse();
            try { await PersistLog(); }
            catch (Exception auditError) { throw ResourceErrors.Combine(failure, auditError); }
            if (!ReferenceEquals(failure, exception)) throw failure;
            throw;
        }
        async Task PersistLog()
        {
            if (requestMetadata.LogBodies is { } bodies) executionBudget.TransferLogBodies(bodies);
            var upstreamBody = executionBudget.TransferToLog(
                convertedState?.UpstreamRequestBody ?? requestMetadata.LogBodies?.UpstreamRequest);
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
                    UpstreamRequestBody = upstreamBody,
                    AggregatedUsage = tools?.HasAccountingUsage == true
                        ? tools.AccountingUsage
                        : convertedState?.RejectedUsage.Count > 0 ? convertedState.RejectedUsage : null
                },
                requestMetadata);
        }
    }

    private StreamResponseCapture CreateFullCapture(string protocol, RequestExecutionBudget executionBudget,
        CancellationToken cancellationToken, IReadOnlySet<string>? toolNames = null) =>
        new(protocol, new StreamCaptureBudget(_outputFrames.MaxCaptureBytes, _outputFrames.MaxCaptureItems,
            _outputFrames.MaxCaptureFields, executionBudget.AuditCapture, cancellationToken), toolNames,
            maxEventBytes: RequestExecutionBudget.AcceptedSourceFrameBytes(protocol));

    private static StreamResponseCaptureResult CompleteCapture(
        StreamResponseCapture capture,
        StreamCaptureTermination termination) =>
        ProtocolConversion.ProtocolConversionErrors.Wrap(() => capture.Complete(termination));

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
            if (capture is not null)
            {
                ProtocolConversion.ProtocolConversionErrors.Wrap(() => capture.Accept(frame));
            }
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
            ProtocolConversion.ProtocolConversionErrors.Wrap(() => capture.Accept(line));
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

using System.Diagnostics;
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

public sealed class ProxyNonStreamService : IProxyNonStreamService
{
    private readonly IUpstreamClient _upstream;
    private readonly IProxyLogService _logs;
    private readonly IWebSearchToolExecutor _webSearch;
    private readonly WebSearchContinuationStore _webSearchHistory;
    private readonly ProxyAuditSpool _auditSpool;

    public ProxyNonStreamService(
        IUpstreamClient upstream,
        IProxyLogService logs,
        IWebSearchToolExecutor webSearch,
        WebSearchContinuationStore webSearchHistory,
        ProxyAuditSpool auditSpool)
    {
        _upstream = upstream;
        _logs = logs;
        _webSearch = webSearch;
        _webSearchHistory = webSearchHistory;
        ArgumentNullException.ThrowIfNull(auditSpool);
        _auditSpool = auditSpool;
    }

    public async Task<ProxyNonStreamResult> SendAsync(ProxyNonStreamContext context)
    {
        using var executionBudget = RequestExecutionBudget.Create(_auditSpool, context.RequestMetadata,
            context.SnapshotReservation, CancellationToken.None);
        var requestMetadata = executionBudget.Attach(context.RequestMetadata);
        var outputFrames = new SseFrameOptions();
        Exception? primaryFailure = null;
        var upstreamRequest = context.UpstreamRequest;
        var upstreamRequestBody = requestMetadata.LogBodies?.UpstreamRequest;
        Dictionary<string, object?>? upstreamResponse = null;
        Dictionary<string, object?>? responsePayload = null;
        Dictionary<string, object?>? webSearchDetails = null;
        object? errorResponse = null;
        var statusCode = ProxyHttpStatus.Ok;
        string? error = null;
        var textFormat = ProtocolConverter.ExtractTextFormat(context.OriginalPayload);
        var toolCallMappings = context.EntryProtocol == ProtocolConverter.Responses
            && (context.ChannelType == ProtocolConverter.Chat
                || context.ChannelType == ProtocolConverter.Messages)
            ? context.ToolCallMappings ?? ProtocolConverter.BuildResponsesToolCallMappings(context.Payload)
            : null;
        BuiltinToolSession? tools = null;
        var rejectedUsage = new Dictionary<string, object?>(StringComparer.Ordinal);
        var retryUsed = false;
        var toolExecutionPhase = false;

        try
        {
            executionBudget.EnsureQueued(requestMetadata, context.OriginalPayload, context.CancellationToken);
            var bodies = requestMetadata.LogBodies ?? new ProxyLogBodyHandles(null, null);
            requestMetadata = requestMetadata.WithLogBodies(bodies with
            {
                Headers = bodies.Headers ?? executionBudget.CaptureAuditJson(requestMetadata.Headers, context.CancellationToken),
                Request = bodies.Request ?? (requestMetadata.RawBody is { Length: > 0 } raw
                    ? executionBudget.CaptureAuditUtf8(raw.Span, context.CancellationToken)
                    : executionBudget.CaptureAuditJson(context.OriginalPayload, context.CancellationToken))
            });
            using var toolLifetime = tools = context.BuiltinTools is null ? null : new BuiltinToolSession(
                context.BuiltinTools, _webSearch, _webSearchHistory,
                context.ChannelType, context.DefaultTimeout, BuiltinToolSession.OutputTokenBudget(context.Payload))
            {
                BeforeToolExecution = (sourceRequest, details, token) => executionBudget.EnsureTool(
                    details, sourceRequest, outputFrames, context.ChannelType, isStream: false, token),
                AfterToolExecution = RequestExecutionBudget.ValidateToolResult
            };
            while (true)
            {
                executionBudget.EnsureCall(requestMetadata, context.OriginalPayload, upstreamRequest, outputFrames, context.ChannelType,
                    isStream: false, tools?.Token(context.CancellationToken) ?? context.CancellationToken,
                    knownAuditDetails: tools?.Details, hasBuiltinTools: tools is not null);
                upstreamRequestBody ??= executionBudget.CaptureAuditJson(upstreamRequest, context.CancellationToken);
                upstreamResponse = await _upstream.PostJsonAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.DefaultTimeout,
                    tools?.Token(context.CancellationToken) ?? context.CancellationToken);
                try
                {
                    RequestExecutionBudget.ValidateCompleteJson(upstreamResponse, "source", outputFrames.MaxCaptureBytes,
                        context.CancellationToken);
                }
                catch (StorageCapacityException)
                {
                    // A custom IUpstreamClient returning an over-C dictionary violated its
                    // source contract. It has no original-wire evidence to fabricate. The
                    // real HTTP adapter rejects earlier with bounded received-json evidence.
                    upstreamResponse = null;
                    throw;
                }
                try
                {
                    responsePayload = ProtocolConversion.ProtocolConversionErrors.Wrap(
                        () => ProtocolConverter.ConvertOwnedResponse(
                        upstreamResponse,
                        context.EntryProtocol,
                        context.ChannelType,
                        context.Route.OriginalModel,
                        textFormat,
                        toolCallMappings));
                    if (tools is null && rejectedUsage.Count > 0 && responsePayload is not null)
                    {
                        BuiltinToolSession.AddUsage(
                            rejectedUsage,
                            upstreamResponse is null
                                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                                : WebSearchPayload.ObjectValue(upstreamResponse, "usage"));
                    }
                }
                catch (UpstreamException exception)
                    when (toolCallMappings is not null
                          && ToolContractRetryPolicy.IsUndeclaredToolCall(exception))
                {
                    if (tools is not null)
                    {
                        tools.RecordRejectedRound(upstreamResponse, exception.Message);
                    }
                    else
                    {
                        BuiltinToolSession.AddUsage(
                            rejectedUsage,
                            upstreamResponse is null
                                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                                : WebSearchPayload.ObjectValue(upstreamResponse, "usage"));
                    }

                    if (retryUsed)
                    {
                        throw ToolContractRetryPolicy.RetryExhausted(exception);
                    }

                    retryUsed = true;
                    upstreamRequest = ToolContractRetryPolicy.BuildRetryRequest(
                        upstreamRequest,
                        context.ChannelType,
                        toolCallMappings?.Keys ?? Array.Empty<string>(),
                        tools?.Binding.WebSearchToolName);
                    upstreamRequestBody = null; // The next model-call admission validates P and reserves this version.
                    continue;
                }
                try
                {
                    RequestExecutionBudget.ValidateCompleteJson(responsePayload, "downstream", outputFrames.MaxCaptureBytes,
                        context.CancellationToken);
                }
                catch (StorageCapacityException)
                {
                    responsePayload = null; // The failed response is logged instead of an over-C success candidate.
                    throw;
                }
                if (tools is null)
                {
                    break;
                }
                toolExecutionPhase = true;
                await foreach (var _ in tools.ProcessRoundAsync(
                    upstreamRequest, upstreamResponse, responsePayload, context.CancellationToken))
                {
                }
                toolExecutionPhase = false;
                responsePayload = tools.ResponsePayload;
                if (tools.NextRequest is not { } next)
                {
                    break;
                }
                upstreamRequest = next;
                upstreamRequestBody = null; // The next model-call admission validates P and reserves this version.
            }

            try
            {
                RequestExecutionBudget.ValidateCompleteJson(responsePayload, "downstream", outputFrames.MaxCaptureBytes,
                    context.CancellationToken);
            }
            catch (StorageCapacityException)
            {
                responsePayload = null;
                throw;
            }
            return new ProxyNonStreamResult(statusCode, responsePayload);
        }
        catch (Exception failure) when (ResourceErrors.Normalize(failure) is ProxyException)
        {
            var exception = (ProxyException)ResourceErrors.Normalize(failure);
            primaryFailure = exception;
            statusCode = exception.StatusCode;
            error = exception.Message;
            errorResponse = exception.ToResponse();
            if (!toolExecutionPhase)
                upstreamResponse = UpstreamErrorBody(failure as ProxyException ?? exception) ?? upstreamResponse;
            // Tool failures retain their own bounded source in Details.calls[].raw;
            // never relabel provider JSON as the model's original upstream response.
            return new ProxyNonStreamResult(statusCode, errorResponse, exception);
        }
        catch (OperationCanceledException exception)
        {
            primaryFailure = exception;
            if (context.CancellationToken.IsCancellationRequested)
            {
                statusCode = ProxyHttpStatus.ClientClosedRequest;
                error = ProxyRequestStatusResolver.ClientCancelledError;
                throw;
            }
            statusCode = ProxyHttpStatus.GatewayTimeout;
            error = "proxy request timed out";
            var timeout = new UpstreamException(error, statusCode);
            errorResponse = timeout.ToResponse();
            return new ProxyNonStreamResult(statusCode, errorResponse, timeout);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            statusCode = ProxyHttpStatus.InternalServerError;
            error = exception.Message;
            throw;
        }
        finally
        {
            try
            {
                webSearchDetails = tools?.Details;
                if (requestMetadata.LogBodies is { } bodies) executionBudget.TransferLogBodies(bodies);
                upstreamRequestBody = executionBudget.TransferToLog(upstreamRequestBody);
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
                        IsStream: false,
                        TtftMs: null,
                        statusCode,
                        ElapsedMilliseconds(context.StartedTimestamp),
                        error,
                        webSearchDetails)
                    {
                        UpstreamRequestBody = upstreamRequestBody,
                        AggregatedUsage = tools?.HasAccountingUsage == true
                            ? tools.AccountingUsage
                            : rejectedUsage.Count > 0 ? rejectedUsage : null
                    },
                    requestMetadata);
            }
            catch (Exception auditFailure) when (primaryFailure is not null)
            {
                throw ResourceErrors.Combine(primaryFailure, auditFailure);
            }
        }
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
            if (upstream.Body is Dictionary<string, object?> evidence
                && (ReceivedFrameEvidence.IsEvidence(evidence) || UpstreamJsonEvidence.IsEvidence(evidence)))
                return evidence;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["error"] = upstream.Body
            };
        }

        return null;
    }
}

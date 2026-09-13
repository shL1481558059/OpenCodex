using System.Diagnostics;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
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

    public ProxyNonStreamService(
        IUpstreamClient upstream,
        IProxyLogService logs,
        IWebSearchToolExecutor webSearch,
        WebSearchContinuationStore webSearchHistory)
    {
        _upstream = upstream;
        _logs = logs;
        _webSearch = webSearch;
        _webSearchHistory = webSearchHistory;
    }

    public async Task<ProxyNonStreamResult> SendAsync(ProxyNonStreamContext context)
    {
        var upstreamRequest = context.UpstreamRequest;
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
            ? ProtocolConverter.BuildResponsesToolCallMappings(context.Payload)
            : null;
        BuiltinToolSession? tools = null;

        try
        {
            using var toolLifetime = tools = context.BuiltinTools is null ? null : new BuiltinToolSession(
                context.BuiltinTools, _webSearch, _webSearchHistory,
                context.ChannelType, context.DefaultTimeout, BuiltinToolSession.OutputTokenBudget(context.Payload));
            while (true)
            {
                upstreamResponse = await _upstream.PostJsonAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.DefaultTimeout,
                    tools?.Token(context.CancellationToken) ?? context.CancellationToken);
                responsePayload = ProtocolConverter.ConvertResponse(
                    upstreamResponse,
                    context.EntryProtocol,
                    context.ChannelType,
                    context.Route.OriginalModel,
                    textFormat,
                    toolCallMappings);
                if (tools is null)
                {
                    break;
                }
                await foreach (var _ in tools.ProcessRoundAsync(
                    upstreamRequest, upstreamResponse, responsePayload, context.CancellationToken))
                {
                }
                responsePayload = tools.ResponsePayload;
                if (tools.NextRequest is not { } next)
                {
                    break;
                }
                upstreamRequest = next;
            }

            return new ProxyNonStreamResult(statusCode, responsePayload);
        }
        catch (ProxyException exception)
        {
            statusCode = exception.StatusCode;
            error = exception.Message;
            errorResponse = exception.ToResponse();
            upstreamResponse = UpstreamErrorBody(exception);
            return new ProxyNonStreamResult(statusCode, errorResponse, exception);
        }
        catch (OperationCanceledException)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                statusCode = ProxyHttpStatus.ClientClosedRequest;
                error = "client cancelled the request";
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
            statusCode = ProxyHttpStatus.InternalServerError;
            error = exception.Message;
            throw;
        }
        finally
        {
            webSearchDetails = tools?.Details;
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
                    AggregatedUsage = tools?.HasCalls == true ? tools.AccountingUsage : null
                },
                context.RequestMetadata);
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
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["error"] = upstream.Body
            };
        }

        return null;
    }
}

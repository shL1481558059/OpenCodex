using System.Diagnostics;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyNonStreamService : IProxyNonStreamService
{
    private readonly IUpstreamClient _upstream;
    private readonly IProxyLogService _logs;
    private readonly IWebSearchSimulator _webSearch;

    public ProxyNonStreamService(
        IUpstreamClient upstream,
        IProxyLogService logs,
        IWebSearchSimulator webSearch)
    {
        _upstream = upstream;
        _logs = logs;
        _webSearch = webSearch;
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

        try
        {
            if (_webSearch.CanSimulate(
                context.EntryProtocol,
                context.ChannelType,
                context.OwnerRole,
                context.Payload))
            {
                try
                {
                    var simulation = await _webSearch.RunAsync(
                        context.Route.Channel,
                        upstreamRequest,
                        context.Payload,
                        context.Route.OriginalModel,
                        context.DefaultTimeout,
                        context.CancellationToken);
                    upstreamRequest = simulation.FinalUpstreamRequest;
                    upstreamResponse = simulation.FinalUpstreamResponse;
                    responsePayload = simulation.ResponsePayload;
                    webSearchDetails = simulation.Details;
                }
                catch (WebSearchSimulationUpstreamException exception)
                {
                    statusCode = exception.ProxyException.StatusCode;
                    error = exception.ProxyException.Message;
                    errorResponse = exception.ProxyException.ToResponse();
                    upstreamRequest = exception.FinalUpstreamRequest;
                    webSearchDetails = exception.Details;
                    return new ProxyNonStreamResult(statusCode, errorResponse, exception.ProxyException);
                }
            }
            else
            {
                upstreamResponse = await _upstream.PostJsonAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.DefaultTimeout,
                    context.CancellationToken);
                var textFormat = ProxyStreamService.ExtractTextFormat(context.OriginalPayload);
                responsePayload = ProtocolConverter.ConvertResponse(
                    upstreamResponse,
                    context.EntryProtocol,
                    context.ChannelType,
                    context.Route.OriginalModel,
                    textFormat);
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
                    IsStream: false,
                    TtftMs: null,
                    statusCode,
                    ElapsedMilliseconds(context.StartedTimestamp),
                    error,
                    webSearchDetails),
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

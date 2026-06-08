using System.Diagnostics;
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
        context.StreamWriter.PrepareSse();

        var ttftStarted = Stopwatch.GetTimestamp();
        var ttftMs = (int?)null;
        var error = (string?)null;
        Dictionary<string, object?>? webSearchDetails = null;
        Dictionary<string, object?>? upstreamResponse = null;
        Dictionary<string, object?>? responsePayload = null;
        var upstreamRequest = context.UpstreamRequest;

        try
        {
            if (_webSearch.CanSimulate(
                context.EntryProtocol,
                context.ChannelType,
                context.OwnerRole,
                context.Payload)
                && context.ChannelType == ProtocolConverter.Chat)
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
                    context.CancellationToken);
                ttftMs = await context.StreamWriter.WriteLinesAsync(
                    streamLines,
                    SseStreamConverter.CountsForTtft,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);

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
                ttftMs = await context.StreamWriter.WriteLinesAsync(
                    streamLines,
                    static line => line.Trim().Length > 0,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);
            }
            else
            {
                var converted = new ConvertedStreamResult();
                var visibleModel = VisibleModel(context);
                var streamLines = _upstream.StreamJsonAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.DefaultTimeout,
                    context.CancellationToken);
                var convertedLines = context.ChannelType == ProtocolConverter.Chat
                    ? SseStreamConverter.ChatToResponsesEvents(
                        streamLines,
                        visibleModel,
                        converted,
                        context.CancellationToken)
                    : SseStreamConverter.MessagesToResponsesEvents(
                        streamLines,
                        visibleModel,
                        converted,
                        context.CancellationToken);
                ttftMs = await context.StreamWriter.WriteLinesAsync(
                    convertedLines,
                    SseStreamConverter.CountsForTtft,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);

                upstreamResponse = converted.UpstreamResponse;
                responsePayload = upstreamResponse is null
                    ? null
                    : ProtocolConverter.ConvertResponse(
                        upstreamResponse,
                        context.EntryProtocol,
                        context.ChannelType,
                        context.Route.OriginalModel);
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            throw;
        }
        finally
        {
            _logs.WriteLog(
                new ProxyLogContext(
                    context.RequestId,
                    context.OwnerUsername,
                    context.ApiKeyId,
                    context.Payload,
                    upstreamRequest,
                    upstreamResponse,
                    responsePayload,
                    ErrorResponse: null,
                    context.RequestModel,
                    context.UpstreamModel,
                    context.ChannelId,
                    context.ChannelType,
                    IsStream: true,
                    TtftMs: ttftMs,
                    StatusCode: 200,
                    DurationMs: ElapsedMilliseconds(context.StartedTimestamp),
                    error,
                    webSearchDetails),
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
}

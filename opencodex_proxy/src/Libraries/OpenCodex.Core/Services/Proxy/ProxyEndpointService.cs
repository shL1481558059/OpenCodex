using System.Diagnostics;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyEndpointService : IProxyEndpointService
{
    private readonly IProxyLogService _logs;
    private readonly IProxyRequestService _requests;
    private readonly IProxyRouteService _routes;
    private readonly IProxyNonStreamService _nonStreams;
    private readonly IProxyStreamService _streams;

    public ProxyEndpointService(
        IProxyLogService logs,
        IProxyRequestService requests,
        IProxyRouteService routes,
        IProxyNonStreamService nonStreams,
        IProxyStreamService streams)
    {
        _logs = logs;
        _requests = requests;
        _routes = routes;
        _nonStreams = nonStreams;
        _streams = streams;
    }

    public async Task<ProxyEndpointResult> ProxyAsync(ProxyEndpointContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var requestState = _requests.StartRequest();
        var requestId = requestState.RequestId;
        var ownerUsername = requestState.DefaultOwnerUsername;
        var defaultTimeout = requestState.DefaultTimeout;
        long? apiKeyId = null;
        Dictionary<string, object?>? payload = null;
        Dictionary<string, object?>? upstreamRequest = null;
        string? requestModel = null;
        string? upstreamModel = null;
        string? channelId = null;
        string? channelType = null;
        string? ownerRole = null;
        var statusCode = 200;
        string? error = null;
        object? errorResponse = null;
        var logInFinally = true;
        var requestMetadata = context.RequestMetadata;

        try
        {
            var accessKey = _requests.AuthenticateAccessKey(context.AuthorizationHeader);
            ownerUsername = accessKey.OwnerUsername;
            ownerRole = accessKey.User.Role;
            apiKeyId = accessKey.Id;

            payload = context.Payload;
            if (payload is null)
            {
                throw new BadRequestException("request body must be a JSON object");
            }

            requestModel = JsonDictionaryValue.String(payload, "model");
            var route = _routes.ChooseRoute(ownerUsername, requestModel);
            channelType = JsonDictionaryValue.String(route.Channel, "type");
            channelId = JsonDictionaryValue.String(route.Channel, "id");
            upstreamModel = route.UpstreamModel;

            upstreamRequest = ProtocolConverter.ConvertRequest(
                payload,
                context.EntryProtocol,
                channelType,
                route.UpstreamModel);

            if (payload.TryGetValue("stream", out var streamValue) && streamValue is true)
            {
                if (!ProtocolConverter.SupportsStreamingConversion(context.EntryProtocol, channelType))
                {
                    throw new BadRequestException($"streaming conversion is not migrated for {context.EntryProtocol} to {channelType}");
                }

                logInFinally = false;
                await _streams.StreamAsync(
                    new ProxyStreamContext(
                        started,
                        requestId,
                        ownerUsername,
                        apiKeyId,
                        payload,
                        upstreamRequest,
                        context.EntryProtocol,
                        route,
                        channelType,
                        channelId,
                        ownerRole ?? string.Empty,
                        upstreamModel,
                        requestModel,
                        defaultTimeout,
                        requestMetadata,
                        context.StreamWriter,
                        context.CancellationToken));
                return new ProxyEndpointResult(200, Payload: null, IsEmpty: true);
            }

            logInFinally = false;
            var result = await _nonStreams.SendAsync(
                new ProxyNonStreamContext(
                    started,
                    requestId,
                    ownerUsername,
                    apiKeyId,
                    payload,
                    upstreamRequest,
                    context.EntryProtocol,
                    route,
                    channelType,
                    channelId,
                    ownerRole ?? string.Empty,
                    upstreamModel,
                    requestModel,
                    defaultTimeout,
                    requestMetadata,
                    context.CancellationToken));
            return new ProxyEndpointResult(result.StatusCode, result.Payload, IsEmpty: false);
        }
        catch (ProxyException exception)
        {
            statusCode = exception.StatusCode;
            error = exception.Message;
            errorResponse = exception.ToResponse();
            return new ProxyEndpointResult(statusCode, errorResponse, IsEmpty: false);
        }
        finally
        {
            if (logInFinally)
            {
                _logs.WriteLog(
                    new ProxyLogContext(
                        requestId,
                        ownerUsername,
                        apiKeyId,
                        payload,
                        upstreamRequest,
                        UpstreamResponse: null,
                        ResponsePayload: null,
                        errorResponse,
                        requestModel,
                        upstreamModel,
                        channelId,
                        channelType,
                        IsStream: false,
                        TtftMs: null,
                        statusCode,
                        ElapsedMilliseconds(started),
                        error,
                        WebSearchDetails: null),
                    requestMetadata);
            }
        }
    }

    private static int ElapsedMilliseconds(long started)
    {
        return (int)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MidpointRounding.AwayFromZero);
    }
}

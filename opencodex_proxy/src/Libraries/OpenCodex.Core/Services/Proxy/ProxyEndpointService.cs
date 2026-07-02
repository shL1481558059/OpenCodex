using System.Diagnostics;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyEndpointService : IProxyEndpointService
{
    private const string DefaultResponsesUserAgent =
        "Codex Desktop/0.140.0-alpha.2 (Mac OS 13.7.8; arm64) unknown (Codex Desktop; 26.609.71450)";
    private const string DefaultResponsesOriginator = "Codex Desktop";
    private const string DefaultResponsesBetaFeatures = "terminal_resize_reflow,remote_compaction_v2";

    private static readonly string[] ResponsesPassthroughHeaders =
    [
        "User-Agent",
        "x-oai-attestation",
        "x-codex-turn-metadata",
        "x-codex-window-id",
        "x-client-request-id",
        "originator",
        "session-id",
        "thread-id",
        "x-codex-beta-features"
    ];

    private readonly IProxyLogService _logs;
    private readonly IProxyRequestService _requests;
    private readonly IProxyRouteService _routes;
    private readonly IChannelCapacityService _channelCapacity;
    private readonly IChannelCircuitBreakerService _channelCircuitBreaker;
    private readonly IChannelAffinityService _channelAffinity;
    private readonly IProxyImageFallbackService _imageFallback;
    private readonly IProxyNonStreamService _nonStreams;
    private readonly IProxyStreamService _streams;

    public ProxyEndpointService(
        IProxyLogService logs,
        IProxyRequestService requests,
        IProxyRouteService routes,
        IChannelCapacityService channelCapacity,
        IChannelCircuitBreakerService channelCircuitBreaker,
        IChannelAffinityService channelAffinity,
        IProxyImageFallbackService imageFallback,
        IProxyNonStreamService nonStreams,
        IProxyStreamService streams)
    {
        _logs = logs;
        _requests = requests;
        _routes = routes;
        _channelCapacity = channelCapacity;
        _channelCircuitBreaker = channelCircuitBreaker;
        _channelAffinity = channelAffinity;
        _imageFallback = imageFallback;
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
        Guid? apiKeyId = null;
        Dictionary<string, object?>? payload = null;
        Dictionary<string, object?>? effectivePayload = null;
        Dictionary<string, object?>? upstreamRequest = null;
        Dictionary<string, object?>? upstreamResponse = null;
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
        var streamResponseStarted = false;
        Guid? requestLogId = null;

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
            var requestContainsImages = ProxyImageRequestDetector.ContainsImageInput(payload, context.EntryProtocol);
            var stickyKey = JsonDictionaryValue.String(payload, "prompt_cache_key");
            var isStream = payload.TryGetValue("stream", out var streamValue) && streamValue is true;
            requestLogId = _logs.CreateQueuedLog(new ProxyRequestLogQueuedContext(
                requestId,
                ownerUsername,
                apiKeyId,
                payload,
                requestModel,
                isStream,
                requestMetadata.Method,
                requestMetadata.Path,
                requestMetadata.ClientIp,
                requestMetadata.Headers));
            var candidates = OrderCandidates(
                ownerUsername,
                requestModel,
                requestContainsImages,
                stickyKey);

            ProxyException? lastFailoverException = null;
            var routeAttemptNumber = 0;
            foreach (var candidate in candidates)
            {
                var candidateChannelId = JsonDictionaryValue.String(candidate.Route.Channel, "id");
                var candidateEnabled = !candidate.Route.Channel.TryGetValue("enabled", out var enabledValue) || enabledValue is true;
                var candidateCircuitBreakDuration = TimeSpan.FromSeconds(Math.Max(
                    0,
                    IntValue(candidate.Route.Channel, "circuit_break_duration_seconds", 0)));
                var healthStatus = _channelCircuitBreaker.GetHealthStatus(
                    ownerUsername,
                    candidateChannelId,
                    candidateEnabled,
                    candidateCircuitBreakDuration);
                if (healthStatus == ChannelHealthStatus.Open)
                {
                    continue;
                }

                var halfOpenProbeAcquired = false;
                if (healthStatus == ChannelHealthStatus.HalfOpen)
                {
                    halfOpenProbeAcquired = _channelCircuitBreaker.TryAcquireHalfOpenProbe(
                        ownerUsername,
                        candidateChannelId,
                        candidateCircuitBreakDuration);
                    if (!halfOpenProbeAcquired)
                    {
                        continue;
                    }
                }

                using var capacityLease = _channelCapacity.TryAcquire(ownerUsername, candidate.Route.Channel);
                if (capacityLease is null)
                {
                    if (halfOpenProbeAcquired)
                    {
                        _channelCircuitBreaker.ReleaseHalfOpenProbe(
                            ownerUsername,
                            candidateChannelId,
                            candidateCircuitBreakDuration);
                    }
                    continue;
                }

                TrackingProxyStreamWriter? trackingWriter = null;
                routeAttemptNumber++;
                var attemptStarted = Stopwatch.GetTimestamp();
                string? attemptChannelType = null;
                string? attemptUpstreamModel = null;
                Dictionary<string, object?>? attemptUpstreamRequest = null;
                try
                {
                    if (!string.IsNullOrEmpty(stickyKey))
                    {
                        _channelAffinity.Remember(
                            ownerUsername,
                            stickyKey,
                            candidateChannelId);
                    }

                    var route = candidate.Route;
                    channelType = JsonDictionaryValue.String(route.Channel, "type");
                    channelId = candidateChannelId;
                    upstreamModel = route.UpstreamModel;
                    attemptChannelType = channelType;
                    attemptUpstreamModel = upstreamModel;
                    route = ApplyResponsesPassthroughHeaders(route, context.EntryProtocol, channelType, requestMetadata);

                    effectivePayload = payload;
                    if (requestContainsImages
                        && !route.SupportsImage
                        && route.MatchedModelMapping)
                    {
                        var fallback = await _imageFallback.RewriteAsync(new ProxyImageFallbackContext(
                            requestId,
                            ownerUsername,
                            apiKeyId,
                            payload,
                            context.EntryProtocol,
                            requestModel,
                            defaultTimeout,
                            requestMetadata,
                            context.CancellationToken));
                        effectivePayload = fallback.Payload;
                    }

                    var channelCompat = JsonDictionaryValue.Object(route.Channel, "compat", WebSearchPayload.DeepCopyObject);
                    effectivePayload = ChannelCompatRequestRewriter.Apply(
                        effectivePayload,
                        channelCompat).Payload;

                    upstreamRequest = ProtocolConverter.ConvertRequest(
                        effectivePayload,
                        context.EntryProtocol,
                        channelType,
                        route.UpstreamModel,
                        channelCompat);
                    attemptUpstreamRequest = upstreamRequest;

                    if (requestLogId.HasValue)
                    {
                        _logs.MarkProcessing(requestLogId.Value, new ProxyRequestLogProcessingContext(
                            ownerUsername,
                            apiKeyId,
                            upstreamRequest,
                            requestModel,
                            upstreamModel,
                            channelId,
                            channelType,
                            isStream));
                    }

                    if (isStream)
                    {
                        if (!ProtocolConverter.SupportsStreamingConversion(context.EntryProtocol, channelType))
                        {
                            throw new BadRequestException($"streaming conversion is not migrated for {context.EntryProtocol} to {channelType}");
                        }

                        logInFinally = false;
                        trackingWriter = new TrackingProxyStreamWriter(context.StreamWriter);
                        await _streams.StreamAsync(
                            new ProxyStreamContext(
                                started,
                                requestLogId ?? Guid.Empty,
                                requestId,
                                ownerUsername,
                                apiKeyId,
                                payload,
                                effectivePayload,
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
                                trackingWriter,
                                context.CancellationToken));

                        WriteChannelAttemptLog(
                            requestLogId,
                            requestId,
                            ownerUsername,
                            apiKeyId,
                            payload,
                            attemptUpstreamRequest,
                            requestModel,
                            attemptUpstreamModel,
                            candidate.Route.Channel,
                            candidateChannelId,
                            attemptChannelType,
                            isStream,
                            routeAttemptNumber,
                            attemptStarted,
                            ProxyHttpStatus.Ok,
                            error: null,
                            failoverEligible: false,
                            upstreamResponse: null,
                            requestMetadata);
                        _channelCircuitBreaker.RecordSuccess(ownerUsername, candidateChannelId);
                        streamResponseStarted = trackingWriter.HasWritten;
                        return new ProxyEndpointResult(200, Payload: null, IsEmpty: true);
                    }

                    logInFinally = false;
                    var result = await _nonStreams.SendAsync(
                        new ProxyNonStreamContext(
                            started,
                            requestLogId ?? Guid.Empty,
                            requestId,
                            ownerUsername,
                            apiKeyId,
                            payload,
                            effectivePayload,
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

                    if (result.FailureException is ProxyException failureException)
                    {
                        throw failureException;
                    }

                    WriteChannelAttemptLog(
                        requestLogId,
                        requestId,
                        ownerUsername,
                        apiKeyId,
                        payload,
                        attemptUpstreamRequest,
                        requestModel,
                        attemptUpstreamModel,
                        candidate.Route.Channel,
                        candidateChannelId,
                        attemptChannelType,
                        isStream,
                        routeAttemptNumber,
                        attemptStarted,
                        result.StatusCode,
                        error: null,
                        failoverEligible: false,
                        upstreamResponse: null,
                        requestMetadata);
                    _channelCircuitBreaker.RecordSuccess(ownerUsername, candidateChannelId);
                    return new ProxyEndpointResult(result.StatusCode, result.Payload, IsEmpty: false);
                }
                catch (ProxyException exception)
                {
                    var counted = _channelCircuitBreaker.RecordFailure(
                        ownerUsername,
                        candidateChannelId,
                        exception,
                        candidateCircuitBreakDuration);
                    if (halfOpenProbeAcquired && !counted)
                    {
                        _channelCircuitBreaker.ReleaseHalfOpenProbe(
                            ownerUsername,
                            candidateChannelId,
                            candidateCircuitBreakDuration);
                    }

                    var upstreamErrorResponse = UpstreamErrorBody(exception);
                    if (isStream)
                    {
                        streamResponseStarted = trackingWriter?.HasWritten == true;
                        var failoverEligible = !streamResponseStarted
                            && ProxyFailoverPolicy.CanFailover(exception);
                        WriteChannelAttemptLog(
                            requestLogId,
                            requestId,
                            ownerUsername,
                            apiKeyId,
                            payload,
                            attemptUpstreamRequest,
                            requestModel,
                            attemptUpstreamModel,
                            candidate.Route.Channel,
                            candidateChannelId,
                            attemptChannelType,
                            isStream,
                            routeAttemptNumber,
                            attemptStarted,
                            exception.StatusCode,
                            exception.Message,
                            failoverEligible,
                            upstreamErrorResponse,
                            requestMetadata);
                        if (failoverEligible)
                        {
                            lastFailoverException = exception;
                            continue;
                        }

                        throw;
                    }

                    var canFailover = ProxyFailoverPolicy.CanFailover(exception);
                    WriteChannelAttemptLog(
                        requestLogId,
                        requestId,
                        ownerUsername,
                        apiKeyId,
                        payload,
                        attemptUpstreamRequest,
                        requestModel,
                        attemptUpstreamModel,
                        candidate.Route.Channel,
                        candidateChannelId,
                        attemptChannelType,
                        isStream,
                        routeAttemptNumber,
                        attemptStarted,
                        exception.StatusCode,
                        exception.Message,
                        canFailover,
                        upstreamErrorResponse,
                        requestMetadata);
                    if (canFailover)
                    {
                        lastFailoverException = exception;
                        continue;
                    }

                    throw;
                }
                catch (Exception exception)
                {
                    if (halfOpenProbeAcquired)
                    {
                        _channelCircuitBreaker.ReleaseHalfOpenProbe(
                            ownerUsername,
                            candidateChannelId,
                            candidateCircuitBreakDuration);
                    }

                    if (exception is not OperationCanceledException)
                    {
                        WriteChannelAttemptLog(
                            requestLogId,
                            requestId,
                            ownerUsername,
                            apiKeyId,
                            payload,
                            attemptUpstreamRequest,
                            requestModel,
                            attemptUpstreamModel,
                            candidate.Route.Channel,
                            candidateChannelId,
                            attemptChannelType,
                            isStream,
                            routeAttemptNumber,
                            attemptStarted,
                            ProxyHttpStatus.InternalServerError,
                            exception.Message,
                            failoverEligible: false,
                            upstreamResponse: null,
                            requestMetadata);
                    }

                    throw;
                }
            }

            if (lastFailoverException is not null)
            {
                throw lastFailoverException;
            }

            var modelLabel = string.IsNullOrWhiteSpace(requestModel)
                ? "requested route"
                : $"model {requestModel.Trim()}";
            throw new RoutingException(
                $"all enabled channels for {modelLabel} are at capacity",
                ProxyHttpStatus.TooManyRequests);
        }
        catch (ProxyException exception)
        {
            if (streamResponseStarted)
            {
                throw;
            }

            // UpstreamException 携带上游原始状态码和错误细节，仅供日志记录；
            // 返回给客户端时统一使用 502，避免暴露上游/渠道内部信息。
            statusCode = exception is UpstreamException
                ? ProxyHttpStatus.BadGateway
                : exception.StatusCode;
            error = exception.Message;
            errorResponse = exception.ToResponse();
            upstreamResponse = UpstreamErrorBody(exception);
            return new ProxyEndpointResult(statusCode, errorResponse, IsEmpty: false);
        }
        finally
        {
            if (logInFinally)
            {
                var logContext = new ProxyLogContext(
                        requestId,
                        ownerUsername,
                        apiKeyId,
                        payload,
                        upstreamRequest,
                        UpstreamResponse: upstreamResponse,
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
                        WebSearchDetails: null);
                if (requestLogId.HasValue)
                {
                    _logs.CompleteLog(requestLogId.Value, logContext, requestMetadata);
                }
                else
                {
                    _logs.WriteLog(logContext, requestMetadata);
                }
            }
        }
    }

    private IReadOnlyList<OrderedRouteCandidate> OrderCandidates(
        string ownerUsername,
        string? requestModel,
        bool requestContainsImages,
        string? stickyKey)
    {
        var candidates = _routes.ListRouteCandidates(ownerUsername, requestModel, requestContainsImages);
        var preferredChannelId = string.IsNullOrEmpty(stickyKey)
            ? null
            : _channelAffinity.GetPreferredChannelId(ownerUsername, stickyKey);
        var orderedCandidates = candidates
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Priority = PriorityValue(candidate.Channel),
                ActiveRequests = _channelCapacity.GetActiveRequests(
                    ownerUsername,
                    JsonDictionaryValue.String(candidate.Channel, "id")),
                Order = index,
                IsPreferred = preferredChannelId is not null
                    && string.Equals(
                        JsonDictionaryValue.String(candidate.Channel, "id"),
                        preferredChannelId,
                        StringComparison.Ordinal)
            })
            .OrderByDescending(item => item.IsPreferred)
            .ThenBy(item => item.Priority)
            .ThenBy(item => item.ActiveRequests)
            .ThenBy(item => item.Order);

        return orderedCandidates
            .Select(item => new OrderedRouteCandidate(item.Candidate))
            .ToList();
    }

    private void WriteChannelAttemptLog(
        Guid? parentRequestLogId,
        string requestId,
        string ownerUsername,
        Guid? apiKeyId,
        Dictionary<string, object?>? payload,
        Dictionary<string, object?>? upstreamRequest,
        string? requestModel,
        string? upstreamModel,
        IReadOnlyDictionary<string, object?> channel,
        string? channelId,
        string? channelType,
        bool isStream,
        int routeAttemptNumber,
        long attemptStarted,
        int statusCode,
        string? error,
        bool failoverEligible,
        Dictionary<string, object?>? upstreamResponse,
        ProxyRequestMetadata requestMetadata)
    {
        if (!parentRequestLogId.HasValue)
        {
            return;
        }

        var attemptDetails = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "channel_attempt",
            ["route_attempt_number"] = routeAttemptNumber,
            ["route_retry_number"] = Math.Max(0, routeAttemptNumber - 1),
            ["channel_id"] = channelId,
            ["channel_name"] = JsonDictionaryValue.String(channel, "name"),
            ["channel_type"] = channelType,
            ["upstream_model"] = upstreamModel,
            ["configured_retry_count"] = JsonDictionaryValue.Get(channel, "retry_count"),
            ["status_code"] = statusCode,
            ["outcome"] = statusCode >= 400 || !string.IsNullOrWhiteSpace(error) ? "failed" : "success",
            ["failover_eligible"] = failoverEligible,
            ["duration_ms"] = ElapsedMilliseconds(attemptStarted)
        };
        if (!string.IsNullOrWhiteSpace(error))
        {
            attemptDetails["error"] = error;
        }

        _logs.WriteLog(
            new ProxyLogContext(
                requestId,
                ownerUsername,
                apiKeyId,
                payload,
                upstreamRequest,
                upstreamResponse,
                ResponsePayload: attemptDetails,
                ErrorResponse: null,
                requestModel,
                upstreamModel,
                channelId,
                channelType,
                isStream,
                TtftMs: null,
                statusCode,
                ElapsedMilliseconds(attemptStarted),
                error,
                WebSearchDetails: null,
                RequestType: ProxyRequestTypes.Attempt,
                ParentRequestLogId: parentRequestLogId),
            requestMetadata);
    }

    internal static ProxyRouteDto ApplyResponsesPassthroughHeaders(
        ProxyRouteDto route,
        string entryProtocol,
        string channelType,
        ProxyRequestMetadata requestMetadata)
    {
        if (entryProtocol != ProtocolConverter.Responses || channelType != ProtocolConverter.Responses)
        {
            return route;
        }

        var passthroughHeaders = ResponsesPassthroughHeaders
            .Select(headerName => TryGetResponsesHeader(requestMetadata.Headers, headerName, out var value)
                ? (Name: headerName, Value: value)
                : (Name: (string?)null, Value: (string?)null))
            .Where(item => !string.IsNullOrEmpty(item.Name) && !string.IsNullOrEmpty(item.Value))
            .ToList();

        var channel = WebSearchPayload.DeepCopyObject(route.Channel);
        var headers = WebSearchPayload.TryAsObject(JsonDictionaryValue.Get(channel, "headers"), out var existingHeaders)
            ? existingHeaders
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in passthroughHeaders)
        {
            if (!ContainsHeader(headers, name!))
            {
                headers[name!] = value;
            }
        }

        channel["headers"] = headers;
        return new ProxyRouteDto(
            channel,
            route.OriginalModel,
            route.UpstreamModel,
            route.SupportsImage,
            route.MatchedModelMapping);
    }

    private static bool TryGetResponsesHeader(
        IReadOnlyDictionary<string, string> headers,
        string headerName,
        out string value)
    {
        if (TryGetHeader(headers, headerName, out var requestValue))
        {
            if (string.Equals(headerName, "User-Agent", StringComparison.OrdinalIgnoreCase)
                && !requestValue.Contains("Codex Desktop", StringComparison.OrdinalIgnoreCase))
            {
                value = DefaultResponsesUserAgent;
                return true;
            }

            value = requestValue;
            return true;
        }

        value = DefaultResponsesHeaderValue(headerName);
        return value.Length > 0;
    }

    private static string DefaultResponsesHeaderValue(string headerName)
    {
        if (string.Equals(headerName, "User-Agent", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultResponsesUserAgent;
        }

        if (string.Equals(headerName, "originator", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultResponsesOriginator;
        }

        if (string.Equals(headerName, "x-oai-attestation", StringComparison.OrdinalIgnoreCase))
        {
            return "test-attestation";
        }

        if (string.Equals(headerName, "x-codex-turn-metadata", StringComparison.OrdinalIgnoreCase))
        {
            return """{"session_id":"test-session","thread_id":"test-thread","thread_source":"user","turn_id":"test-turn","request_kind":"turn","window_id":"test-window"}""";
        }

        if (string.Equals(headerName, "x-codex-window-id", StringComparison.OrdinalIgnoreCase))
        {
            return "test-window";
        }

        if (string.Equals(headerName, "x-client-request-id", StringComparison.OrdinalIgnoreCase))
        {
            return "test-request";
        }

        if (string.Equals(headerName, "session-id", StringComparison.OrdinalIgnoreCase))
        {
            return "test-session";
        }

        if (string.Equals(headerName, "thread-id", StringComparison.OrdinalIgnoreCase))
        {
            return "test-thread";
        }

        if (string.Equals(headerName, "x-codex-beta-features", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultResponsesBetaFeatures;
        }

        return string.Empty;
    }

    private static bool TryGetHeader(
        IReadOnlyDictionary<string, string> headers,
        string headerName,
        out string value)
    {
        foreach (var (key, headerValue) in headers)
        {
            if (string.Equals(key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                value = headerValue;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool ContainsHeader(IReadOnlyDictionary<string, object?> headers, string headerName)
    {
        foreach (var key in headers.Keys)
        {
            if (string.Equals(key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int PriorityValue(IReadOnlyDictionary<string, object?> channel)
    {
        if (!channel.TryGetValue("priority", out var value)
            || value is null)
        {
            return 0;
        }

        return value switch
        {
            int priority => priority,
            long priority => (int)priority,
            short priority => priority,
            byte priority => priority,
            _ => 0
        };
    }

    private static int IntValue(IReadOnlyDictionary<string, object?> dictionary, string key, int fallback)
    {
        if (!dictionary.TryGetValue(key, out var value)
            || value is null)
        {
            return fallback;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            _ => fallback
        };
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

    private sealed record OrderedRouteCandidate(ProxyRouteDto Route);
}

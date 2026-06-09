using System.Net;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.ExternalIntegrations;

public sealed partial class HttpUpstreamClient : IUpstreamClient, IUpstreamModelClient
{
    private static readonly IReadOnlyDictionary<string, string> Endpoints =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["responses"] = "/responses",
            ["chat"] = "/chat/completions",
            ["messages"] = "/messages"
        };

    private static readonly HashSet<HttpStatusCode> RetryableStatuses =
    [
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    ];

    private readonly HttpClient _httpClient;

    public HttpUpstreamClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Dictionary<string, object?>> PostJsonAsync(
        IReadOnlyDictionary<string, object?> channel,
        IReadOnlyDictionary<string, object?> payload,
        int defaultTimeout,
        CancellationToken cancellationToken)
    {
        var channelType = JsonDictionaryValue.String(channel, "type");
        if (!Endpoints.TryGetValue(channelType, out var endpoint))
        {
            throw new BadRequestException($"unsupported upstream protocol: {channelType}");
        }

        var timeout = TimeoutValue(JsonDictionaryValue.Get(channel, "timeout_seconds"), defaultTimeout);
        var retryCount = RetryCountValue(JsonDictionaryValue.Get(channel, "retry_count"));
        Exception? lastException = null;
        HttpResponseMessage? lastResponse = null;

        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            using var request = BuildRequest(channel, payload, endpoint);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    timeoutCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    return await ReadJsonObject(response, channel, cancellationToken);
                }

                if (attempt >= retryCount || !RetryableStatuses.Contains(response.StatusCode))
                {
                    await ThrowHttpError(response, channel, cancellationToken);
                }

                lastResponse?.Dispose();
                lastResponse = response;
                response = null;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
                if (attempt >= retryCount)
                {
                    throw new UpstreamException(
                        "upstream request timed out",
                        ProxyHttpStatus.GatewayTimeout,
                        channelId: JsonDictionaryValue.String(channel, "id"));
                }
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
                if (attempt >= retryCount)
                {
                    throw new UpstreamException(
                        $"failed to reach upstream: {exception.Message}",
                        ProxyHttpStatus.BadGateway,
                        channelId: JsonDictionaryValue.String(channel, "id"));
                }
            }
            finally
            {
                response?.Dispose();
            }

            await DelayBeforeRetry(attempt, lastResponse, cancellationToken);
            lastResponse?.Dispose();
            lastResponse = null;
        }

        if (lastException is not null)
        {
            throw new UpstreamException(
                $"failed to reach upstream: {lastException.Message}",
                ProxyHttpStatus.BadGateway,
                channelId: JsonDictionaryValue.String(channel, "id"));
        }

        throw new UpstreamException(
            "failed to reach upstream",
            ProxyHttpStatus.BadGateway,
            channelId: JsonDictionaryValue.String(channel, "id"));
    }

    public async Task<Dictionary<string, object?>> ListModelsAsync(
        IReadOnlyDictionary<string, object?> channel,
        int defaultTimeout,
        CancellationToken cancellationToken)
    {
        var timeout = TimeoutValue(JsonDictionaryValue.Get(channel, "timeout_seconds"), defaultTimeout);
        var retryCount = RetryCountValue(JsonDictionaryValue.Get(channel, "retry_count"));
        Exception? lastException = null;
        HttpResponseMessage? lastResponse = null;

        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            using var request = BuildGetRequest(channel, "/models");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    timeoutCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    return await ReadJsonModelList(response, channel, cancellationToken);
                }

                if (attempt >= retryCount || !RetryableStatuses.Contains(response.StatusCode))
                {
                    await ThrowHttpError(response, channel, cancellationToken);
                }

                lastResponse?.Dispose();
                lastResponse = response;
                response = null;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
                if (attempt >= retryCount)
                {
                    throw new UpstreamException(
                        "upstream request timed out",
                        ProxyHttpStatus.GatewayTimeout,
                        channelId: JsonDictionaryValue.String(channel, "id"));
                }
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
                if (attempt >= retryCount)
                {
                    throw new UpstreamException(
                        $"failed to reach upstream: {exception.Message}",
                        ProxyHttpStatus.BadGateway,
                        channelId: JsonDictionaryValue.String(channel, "id"));
                }
            }
            finally
            {
                response?.Dispose();
            }

            await DelayBeforeRetry(attempt, lastResponse, cancellationToken);
            lastResponse?.Dispose();
            lastResponse = null;
        }

        if (lastException is not null)
        {
            throw new UpstreamException(
                $"failed to reach upstream: {lastException.Message}",
                ProxyHttpStatus.BadGateway,
                channelId: JsonDictionaryValue.String(channel, "id"));
        }

        throw new UpstreamException(
            "failed to reach upstream",
            ProxyHttpStatus.BadGateway,
            channelId: JsonDictionaryValue.String(channel, "id"));
    }

    private static async Task DelayBeforeRetry(
        int attempt,
        HttpResponseMessage? response,
        CancellationToken cancellationToken)
    {
        var retryAfter = response?.Headers.RetryAfter;
        TimeSpan delay;
        if (retryAfter?.Delta is { } delta)
        {
            delay = delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            var computed = date - DateTimeOffset.UtcNow;
            delay = computed > TimeSpan.Zero ? computed : TimeSpan.Zero;
            if (delay > TimeSpan.FromSeconds(30))
            {
                delay = TimeSpan.FromSeconds(30);
            }
        }
        else
        {
            delay = TimeSpan.FromMilliseconds(Math.Min(500 * Math.Pow(2, attempt), 8000));
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }
}

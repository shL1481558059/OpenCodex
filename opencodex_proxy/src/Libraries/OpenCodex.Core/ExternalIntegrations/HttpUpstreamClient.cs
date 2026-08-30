using System.Net;
using System.Net.Http.Headers;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.ExternalIntegrations;

public sealed partial class HttpUpstreamClient : IUpstreamClient, IUpstreamModelClient, IImagesUpstreamClient
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

    // 重试退避参数：任何一次上游重试前至少等待 RetryBaseDelaySeconds，避免立即重打上游。
    private const int RetryBaseDelaySeconds = 2;

    // 指数退避项的上限；叠加抖动后实际最大约 RetryMaxDelaySeconds * (1 + RetryJitterRatio)。
    private const int RetryMaxDelaySeconds = 8;

    // 上游 Retry-After 再长也不会等超过这个值。
    private const int RetryAfterCapSeconds = 30;

    // 抖动只向上浮动，保证"至少隔 RetryBaseDelaySeconds 秒"的语义不被随机数破坏。
    private const double RetryJitterRatio = 0.2;

    private readonly HttpClient _httpClient;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // 退避可注入，供单测断言等待序列而不真的睡眠。
    // 必须保持单一构造函数：AddHttpClient 的 ActivatorUtilities 工厂遇到多个候选构造函数会解析失败。
    public HttpUpstreamClient(
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
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

    private Task DelayBeforeRetry(
        int attempt,
        HttpResponseMessage? response,
        CancellationToken cancellationToken)
    {
        return DelayBeforeRetry(attempt, response?.Headers.RetryAfter, cancellationToken);
    }

    // 单独接受 Retry-After 头，便于流式路径先释放响应再等待。
    private async Task DelayBeforeRetry(
        int attempt,
        RetryConditionHeaderValue? retryAfter,
        CancellationToken cancellationToken)
    {
        await _delay(RetryDelay(attempt, retryAfter), cancellationToken);
    }

    // Retry-After 优先，否则用 base × 2^attempt 指数退避；两条路径都叠加向上抖动，
    // 最后统一夹到 [RetryBaseDelaySeconds, RetryAfterCapSeconds]，保证不会出现零间隔重试。
    private static TimeSpan RetryDelay(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        var suggested = SuggestedRetryDelay(attempt, retryAfter);
        var jittered = suggested * (1 + (Random.Shared.NextDouble() * RetryJitterRatio));
        var floor = TimeSpan.FromSeconds(RetryBaseDelaySeconds);
        if (jittered < floor)
        {
            return floor;
        }

        var ceiling = TimeSpan.FromSeconds(RetryAfterCapSeconds);
        return jittered > ceiling ? ceiling : jittered;
    }

    private static TimeSpan SuggestedRetryDelay(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (retryAfter?.Date is { } date)
        {
            var computed = date - DateTimeOffset.UtcNow;
            return computed > TimeSpan.Zero ? computed : TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(
            Math.Min(RetryBaseDelaySeconds * Math.Pow(2, attempt), RetryMaxDelaySeconds));
    }
}

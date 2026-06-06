using System.Runtime.CompilerServices;
using System.Text;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Errors;

namespace OpenCodex.Api.ExternalIntegrations;

public sealed partial class HttpUpstreamClient
{
    public async IAsyncEnumerable<string> StreamJsonAsync(
        IReadOnlyDictionary<string, object?> channel,
        IReadOnlyDictionary<string, object?> payload,
        int defaultTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channelType = JsonDictionaryValue.String(channel, "type");
        if (!Endpoints.TryGetValue(channelType, out var endpoint))
        {
            throw new BadRequestException($"unsupported upstream protocol: {channelType}");
        }

        var timeout = TimeoutValue(JsonDictionaryValue.Get(channel, "timeout_seconds"), defaultTimeout);
        var retryCount = RetryCountValue(JsonDictionaryValue.Get(channel, "retry_count"));
        HttpResponseMessage? response = null;
        Exception? lastException = null;

        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            using var request = BuildRequest(channel, payload, endpoint);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }

                if (attempt >= retryCount || !RetryableStatuses.Contains(response.StatusCode))
                {
                    await ThrowHttpError(response, channel, cancellationToken);
                }

                await DelayBeforeRetry(attempt, response, cancellationToken);
                response.Dispose();
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
        }

        if (response is null)
        {
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

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                yield return line + "\n";
            }
        }
        finally
        {
            response.Dispose();
        }
    }
}

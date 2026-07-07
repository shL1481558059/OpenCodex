using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.ExternalIntegrations;

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
        StreamReader? reader = null;
        var bufferedLines = new List<string>();
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
                    // 方案 A：探测流开头是否为可重试 SSE error（如 rate_limit_error）。
                    // 读取直到第一条 data: 行，检查其内容；读到的行缓存在 bufferedLines 中。
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    reader = new StreamReader(stream, Encoding.UTF8);
                    bufferedLines.Clear();

                    var retryable = await ProbeStreamForRetryableError(reader, bufferedLines, cancellationToken);
                    if (retryable is not null)
                    {
                        reader.Dispose();
                        reader = null;
                        response.Dispose();
                        response = null;

                        if (attempt >= retryCount)
                        {
                            throw new UpstreamException(
                                retryable.Value.Message,
                                ProxyHttpStatus.TooManyRequests,
                                body: retryable.Value.Body,
                                channelId: JsonDictionaryValue.String(channel, "id"));
                        }

                       await DelayBeforeRetry(attempt, response: null, cancellationToken);
                       continue;
                   }

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

        if (response is null || reader is null)
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

        // 回放探测期间读到的行（在 try-catch 外 yield，符合 C# 语法约束）
        foreach (var line in bufferedLines)
        {
            yield return line;
        }

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
            reader.Dispose();
            response.Dispose();
        }
    }

    // 读取流直到遇到第一条 data: 行（或流结束），检查其内容是否为可重试 SSE error。
    // 读到的所有行加入 bufferedLines，供正常流回放使用。
    private static async Task<(string Message, object? Body)?> ProbeStreamForRetryableError(
        StreamReader reader,
        List<string> bufferedLines,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            bufferedLines.Add(line + "\n");

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line["data:".Length..].TrimStart();
            if (json.Length == 0 || json == "[DONE]")
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                if (TryGetRetryableErrorFromElement(document.RootElement) is { } retryable)
                {
                    return (retryable.Message, FromJsonElement(document.RootElement));
                }
            }
            catch (JsonException)
            {
                // 非 JSON data 行，不视为可重试错误
            }

            // 第一条 data 行不是可重试 error，探测完成
            return null;
        }
    }
}

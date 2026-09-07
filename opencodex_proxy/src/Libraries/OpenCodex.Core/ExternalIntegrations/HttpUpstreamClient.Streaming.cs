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
                    // 方案 A：探测流开头，识别可重试 SSE error 或只含响应骨架的空流。
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    reader = new StreamReader(stream, Encoding.UTF8);
                    bufferedLines.Clear();

                    var probe = await ProbeStreamForRetryableError(
                        reader,
                        bufferedLines,
                        timeoutCts.Token);
                    if (probe.Retryable is not null)
                    {
                        // 先取出 Retry-After 再释放响应，避免退避期间占着上游连接。
                        var retryAfter = response.Headers.RetryAfter;
                        reader.Dispose();
                        reader = null;
                        response.Dispose();
                        response = null;

                        if (attempt >= retryCount)
                        {
                            throw new UpstreamException(
                                probe.Retryable.Value.Message,
                                ProxyHttpStatus.TooManyRequests,
                                body: probe.Retryable.Value.Body,
                                channelId: JsonDictionaryValue.String(channel, "id"));
                        }

                        await DelayBeforeRetry(attempt, retryAfter, cancellationToken);
                        continue;
                    }

                    if (probe.EmptySkeleton)
                    {
                        // 只包含 created/in_progress/空行的骨架流对客户端没有可用内容，
                        // 按 retry_count 在当前渠道内部静默重试。
                        var retryAfter = response.Headers.RetryAfter;
                        reader.Dispose();
                        reader = null;
                        response.Dispose();
                        response = null;

                        if (attempt >= retryCount)
                        {
                            throw new UpstreamException(
                                "upstream stream produced no content",
                                ProxyHttpStatus.BadGateway,
                                channelId: JsonDictionaryValue.String(channel, "id"));
                        }

                        await DelayBeforeRetry(attempt, retryAfter, cancellationToken);
                        continue;
                    }

                    break;
                }

                if (attempt >= retryCount || !RetryableStatuses.Contains(response.StatusCode))
                {
                    try
                    {
                        await ThrowHttpError(response, channel, cancellationToken);
                    }
                    finally
                    {
                        response.Dispose();
                    }
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

                await DelayBeforeRetry(attempt, retryAfter: null, cancellationToken);
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

                await DelayBeforeRetry(attempt, retryAfter: null, cancellationToken);
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

    // 读取流直到遇到有效内容或流结束，检查是否只包含可忽略的响应骨架。
    private static async Task<StreamProbeResult> ProbeStreamForRetryableError(
        StreamReader reader,
        List<string> bufferedLines,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return new StreamProbeResult { EmptySkeleton = true };
            }

            bufferedLines.Add(line + "\n");

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                var trimmed = line.Trim();
                if (trimmed.Length != 0
                    && !string.Equals(trimmed, "event: response.created", StringComparison.Ordinal)
                    && !string.Equals(trimmed, "event: response.in_progress", StringComparison.Ordinal))
                {
                    return new StreamProbeResult { EmptySkeleton = false };
                }

                continue;
            }

            var json = line["data:".Length..].TrimStart();
            if (json.Length == 0 || json == "[DONE]")
            {
                // 空 data 行可继续观察；[DONE] 已是明确结束标记，继续等待可能会保持连接。
                if (json == "[DONE]")
                {
                    return new StreamProbeResult { EmptySkeleton = false };
                }

                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                if (TryGetRetryableErrorFromElement(document.RootElement) is { } retryable)
                {
                    return new StreamProbeResult
                    {
                        Retryable = (retryable.Message, FromJsonElement(document.RootElement))
                    };
                }

                var type = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("type", out var typeElement)
                    && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;
                if (type is not ("response.created" or "response.in_progress"))
                {
                    return new StreamProbeResult { EmptySkeleton = false };
                }

                continue;
            }
            catch (JsonException)
            {
                // 非 JSON data 行，不视为可重试错误
                return new StreamProbeResult { EmptySkeleton = false };
            }
        }
    }

    private sealed class StreamProbeResult
    {
        public (string Message, object? Body)? Retryable { get; init; }

        public bool EmptySkeleton { get; init; }
    }
}

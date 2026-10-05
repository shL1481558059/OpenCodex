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
        var policy = StreamTimeoutPolicy.Create(channel, timeout, retryCount, RetryAfterCapSeconds);
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(policy.Total);
        HttpResponseMessage? response = null;
        StreamReader? reader = null;
        var bufferedLines = new List<string>();

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                ThrowIfStreamBudgetExpired(channel, totalCts.Token, cancellationToken);
                using var request = BuildRequest(channel, payload, endpoint);
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
                attemptCts.CancelAfter(policy.FirstContent);
                var ready = false;
                System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter = null;
                try
                {
                    response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        attemptCts.Token);
                    retryAfter = response.Headers.RetryAfter;
                    if (response.IsSuccessStatusCode)
                    {
                        var stream = await response.Content.ReadAsStreamAsync(attemptCts.Token);
                        reader = new StreamReader(stream, Encoding.UTF8);
                        bufferedLines.Clear();
                        var probe = await ProbeStreamForRetryableError(reader, bufferedLines, attemptCts.Token);
                        if (probe.Error is not null)
                        {
                            if (!probe.Error.Retryable || attempt >= retryCount)
                            {
                                throw new UpstreamException(
                                    probe.Error.Message,
                                    probe.Error.StatusCode,
                                    body: probe.Error.Body,
                                    channelId: JsonDictionaryValue.String(channel, "id"));
                            }
                        }
                        else if (probe.EmptySkeleton)
                        {
                            if (attempt >= retryCount)
                            {
                                throw new UpstreamException(
                                    "upstream stream produced no content",
                                    ProxyHttpStatus.BadGateway,
                                    channelId: JsonDictionaryValue.String(channel, "id"));
                            }
                        }
                        else
                        {
                            ready = true;
                        }
                    }
                    else if (attempt >= retryCount || !RetryableStatuses.Contains(response.StatusCode))
                    {
                        await ThrowHttpError(response, channel, attemptCts.Token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    ThrowIfStreamBudgetExpired(channel, totalCts.Token, cancellationToken);
                    if (attempt >= retryCount)
                    {
                        throw StreamTimeout(channel, "upstream first content timed out");
                    }
                }
                catch (HttpRequestException exception)
                {
                    ThrowIfStreamBudgetExpired(channel, totalCts.Token, cancellationToken);
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
                    // 所有失败尝试都在退避之前释放连接；成功尝试由外层 finally 接管。
                    if (!ready)
                    {
                        reader?.Dispose();
                        reader = null;
                        response?.Dispose();
                        response = null;
                    }
                }

                if (ready)
                {
                    break;
                }

                try
                {
                    await DelayBeforeRetry(attempt, retryAfter, totalCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    ThrowIfStreamBudgetExpired(channel, totalCts.Token, cancellationToken);
                    throw;
                }
            }

            // 缓冲回放也位于资源作用域内，消费者在首行停止时仍会释放响应。
            foreach (var line in bufferedLines)
            {
                ThrowIfStreamBudgetExpired(channel, totalCts.Token, cancellationToken);
                yield return line;
            }

            while (true)
            {
                var line = await ReadStreamLineAsync(reader!, policy.Idle, channel, totalCts.Token, cancellationToken);
                if (line is null)
                {
                    break;
                }

                yield return line + "\n";
            }
        }
        finally
        {
            reader?.Dispose();
            response?.Dispose();
        }
    }

    private static async Task<string?> ReadStreamLineAsync(
        StreamReader reader,
        TimeSpan idleTimeout,
        IReadOnlyDictionary<string, object?> channel,
        CancellationToken totalToken,
        CancellationToken cancellationToken)
    {
        ThrowIfStreamBudgetExpired(channel, totalToken, cancellationToken);
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(totalToken);
        idleCts.CancelAfter(idleTimeout);
        try
        {
            return await reader.ReadLineAsync(idleCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfStreamBudgetExpired(channel, totalToken, cancellationToken);
            throw StreamTimeout(channel, "upstream stream idle timed out");
        }
    }

    private static void ThrowIfStreamBudgetExpired(
        IReadOnlyDictionary<string, object?> channel,
        CancellationToken totalToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (totalToken.IsCancellationRequested)
        {
            throw StreamTimeout(channel, "upstream stream total budget exceeded");
        }
    }

    private static UpstreamException StreamTimeout(IReadOnlyDictionary<string, object?> channel, string message)
    {
        return new UpstreamException(message, ProxyHttpStatus.GatewayTimeout,
            channelId: JsonDictionaryValue.String(channel, "id"));
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
                // SSE 元数据和心跳不是正文；必须读到 data 才能判断首段错误。
                if (trimmed.Length != 0
                    && !trimmed.StartsWith(':')
                    && !trimmed.StartsWith("event:", StringComparison.Ordinal)
                    && !trimmed.StartsWith("id:", StringComparison.Ordinal)
                    && !trimmed.StartsWith("retry:", StringComparison.Ordinal))
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
                if (TryGetStreamErrorFromElement(document.RootElement) is { } streamError)
                {
                    return new StreamProbeResult
                    {
                        Error = streamError
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

    // 只在尚未发布正文时重试瞬态错误；错误状态与重试资格分别判定。
    private static StreamError? TryGetStreamErrorFromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || StreamErrorText(root, "type") != "error"
            || !root.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var code = StreamErrorText(error, "code");
        var type = StreamErrorText(error, "type");
        var status = StreamErrorStatus(error) ?? StreamErrorStatus(root)
            ?? StreamErrorStatusFromIdentifier(code) ?? StreamErrorStatusFromIdentifier(type)
            ?? ProxyHttpStatus.BadGateway;
        var message = StreamErrorText(error, "message");
        if (message.Length == 0) message = "upstream stream failed";
        var identifier = code.Length > 0 ? code : type;
        if (identifier.Length > 0) message = $"{message} ({identifier})";
        return new StreamError
        {
            Message = message, Body = FromJsonElement(root), StatusCode = status,
            Retryable = status == ProxyHttpStatus.TooManyRequests || status >= 500
        };
    }

    private static string StreamErrorText(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() ?? "" : "";
    }

    private static int? StreamErrorStatus(JsonElement value)
    {
        foreach (var name in new[] { "status", "status_code", "http_status" })
        {
            if (!value.TryGetProperty(name, out var field)) continue;
            if (field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) && number is >= 400 and <= 599)
                return number;
            if (field.ValueKind == JsonValueKind.String && int.TryParse(field.GetString(), out number) && number is >= 400 and <= 599)
                return number;
        }
        return null;
    }

    private static int? StreamErrorStatusFromIdentifier(string identifier)
    {
        return identifier switch
        {
            "rate_limit_exceeded" or "rate_limit_error" or "too_many_requests" => ProxyHttpStatus.TooManyRequests,
            "invalid_request_error" or "invalid_request" or "bad_request" or "invalid_argument"
                or "validation_error" or "context_length_exceeded" => 400,
            "authentication_error" or "unauthorized" or "invalid_api_key" or "authentication_failed" => 401,
            "permission_error" or "permission_denied" or "forbidden" or "access_denied" => 403,
            _ => null
        };
    }

    private sealed class StreamError
    {
        public required string Message { get; init; }
        public object? Body { get; init; }
        public int StatusCode { get; init; }
        public bool Retryable { get; init; }
    }

    private sealed class StreamProbeResult
    {
        public StreamError? Error { get; init; }
        public bool EmptySkeleton { get; init; }
    }
}

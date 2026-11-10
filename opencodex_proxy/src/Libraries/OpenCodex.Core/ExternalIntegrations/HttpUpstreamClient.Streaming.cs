using OpenCodex.Core.Protocols;
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
        await foreach (var frame in StreamEventsAsync(channel, payload, defaultTimeout, cancellationToken))
        {
            using var lines = new StringReader(frame.WireText);
            while (lines.ReadLine() is { } line) yield return line + "\n";
        }
    }

    public async IAsyncEnumerable<SseFrame> StreamEventsAsync(
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
        var bufferedFrames = new List<SseFrame>();
        var eventOptions = new SseFrameOptions { MaxEventBytes = _streamOptions.MaxEventBytes };
        SseHttpFrameReader? frames = null;
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);

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
                        bufferedFrames.Clear();
                        frames = new SseHttpFrameReader(reader, eventOptions);
                        var probe = await ProbeStreamForRetryableError(frames, bufferedFrames,
                            _streamOptions.MaxProbeBytes,
                            _streamOptions.MaxProbeFrames, attemptCts.Token);
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
            foreach (var frame in bufferedFrames)
            {
                ThrowIfStreamBudgetExpired(channel, totalCts.Token, cancellationToken);
                yield return frame;
            }
            bufferedFrames.Clear();

            while (true)
            {
                var frame = await ReadStreamFrameAsync(frames!, idleCts, policy.Idle, channel, totalCts.Token, cancellationToken);
                if (frame is null) break;
                yield return frame;
            }
        }
        finally
        {
            reader?.Dispose();
            response?.Dispose();
        }
    }

    private static async Task<SseFrame?> ReadStreamFrameAsync(
        SseHttpFrameReader reader,
        CancellationTokenSource idleCts,
        TimeSpan idleTimeout,
        IReadOnlyDictionary<string, object?> channel,
        CancellationToken totalToken,
        CancellationToken cancellationToken)
    {
        ThrowIfStreamBudgetExpired(channel, totalToken, cancellationToken);
        idleCts.CancelAfter(idleTimeout);
        try
        {
            return await reader.ReadAsync(idleCts.Token, idleCts, idleTimeout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfStreamBudgetExpired(channel, totalToken, cancellationToken);
            throw StreamTimeout(channel, "upstream stream idle timed out");
        }
        finally { idleCts.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan); }
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
        SseHttpFrameReader reader, List<SseFrame> buffered,
        int maxBytes, int maxFrames, CancellationToken cancellationToken)
    {
        long bytes = 0;
        while (true)
        {
            var frame = await reader.ReadAsync(cancellationToken);
            if (frame is null) return new StreamProbeResult { EmptySkeleton = true };
            if (Inspect(frame) is { } outcome) return outcome;
        }

        StreamProbeResult? Inspect(SseFrame frame)
        {
            bytes += Encoding.UTF8.GetByteCount(frame.WireText);
            if (bytes > maxBytes || buffered.Count >= maxFrames)
                throw new UpstreamException("upstream pre-content SSE buffer limit exceeded");
            buffered.Add(frame);
            if (frame.Event is null || frame.Malformed && frame.Event.Data is "") return null;
            if (TryGetStreamError(frame.Event.Data) is { } error) return new() { Error = error };
            return frame.Type is "response.created" or "response.in_progress" ? null : new() { EmptySkeleton = false };
        }
    }

    // Fixed-size reads ensure a malicious single physical line cannot allocate an unbounded
    // StreamReader.ReadLine result before the event limit is checked. Queue holds at most one
    // 4096-character read's frames, not the response. Parser survives the pre-content probe.
    private sealed class SseHttpFrameReader(StreamReader reader, SseFrameOptions options)
    {
        private readonly char[] _buffer = new char[4096];
        private readonly Queue<SseFrame> _ready = new();
        private readonly SseFrameReader _parser = new(options);
        private bool _ended;
        public async Task<SseFrame?> ReadAsync(CancellationToken token,
            CancellationTokenSource? idle = null, TimeSpan idleTimeout = default)
        {
            while (_ready.Count == 0 && !_ended)
            {
                idle?.CancelAfter(idleTimeout);
                var count = await reader.ReadAsync(_buffer.AsMemory(), token);
                if (count == 0)
                {
                    _ended = true;
                    if (_parser.Complete() is { } final) _ready.Enqueue(final);
                }
                else
                {
                    try
                    {
                        foreach (var frame in _parser.Feed(new string(_buffer, 0, count))) _ready.Enqueue(frame);
                    }
                    catch (InvalidDataException exception)
                    {
                        throw new UpstreamException(exception.Message, ProxyHttpStatus.BadGateway);
                    }
                }
            }
            return _ready.Count > 0 ? _ready.Dequeue() : null;
        }
    }

    private static StreamError? TryGetStreamError(object? data)
    {
        if (data is not IReadOnlyDictionary<string, object?> root || JsonDictionaryValue.String(root, "type") != "error"
            || JsonDictionaryValue.Get(root, "error") is not IReadOnlyDictionary<string, object?> error) return null;
        var code = JsonDictionaryValue.String(error, "code");
        var type = JsonDictionaryValue.String(error, "type");
        var status = StreamErrorStatus(error) ?? StreamErrorStatus(root)
            ?? StreamErrorStatusFromIdentifier(code) ?? StreamErrorStatusFromIdentifier(type) ?? ProxyHttpStatus.BadGateway;
        var message = JsonDictionaryValue.String(error, "message");
        if (message.Length == 0) message = "upstream stream failed";
        var identifier = code.Length > 0 ? code : type;
        if (identifier.Length > 0) message = $"{message} ({identifier})";
        return new() { Message = message, Body = data, StatusCode = status,
            Retryable = status == ProxyHttpStatus.TooManyRequests || status >= 500 };
    }

    private static int? StreamErrorStatus(IReadOnlyDictionary<string, object?> value)
    {
        foreach (var name in new[] { "status", "status_code", "http_status" })
            if (int.TryParse(JsonDictionaryValue.Get(value, name)?.ToString(), out var status) && status is >= 400 and <= 599) return status;
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

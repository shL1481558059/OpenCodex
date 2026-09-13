using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;

namespace OpenCodex.Core.Services.Proxy;

public sealed partial class ProxyStreamService : IProxyStreamService
{
    private readonly IUpstreamClient _upstream;
    private readonly IProxyLogService _logs;
    private readonly IWebSearchToolExecutor _webSearch;
    private readonly WebSearchContinuationStore _webSearchHistory;

    public ProxyStreamService(
        IUpstreamClient upstream,
        IProxyLogService logs,
        IWebSearchToolExecutor webSearch,
        WebSearchContinuationStore webSearchHistory)
    {
        _upstream = upstream;
        _logs = logs;
        _webSearch = webSearch;
        _webSearchHistory = webSearchHistory;
    }

    public async Task StreamAsync(ProxyStreamContext context)
    {
        context.UpstreamRequest["stream"] = true;

        var ttftStarted = Stopwatch.GetTimestamp();
        StreamWriteMetrics? streamWriteMetrics = null;
        var ttftMs = (int?)null;
        var error = (string?)null;
        object? errorResponse = null;
        Dictionary<string, object?>? webSearchDetails = null;
        Dictionary<string, object?>? upstreamResponse = null;
        Dictionary<string, object?>? responsePayload = null;
        StreamResponseCapture? passThroughResponseCapture = null;
        var passThroughTermination = StreamCaptureTermination.UnexpectedEnd;
        var streamLineCaptures = new List<ProxyRequestStreamLineCapture>();
        var statusCode = ProxyHttpStatus.Ok;
        var upstreamRequest = context.UpstreamRequest;
        ConvertedProxyStreamState? convertedState = null;
        BuiltinToolSession? tools = null;
        try
        {
            using var toolLifetime = tools = context.BuiltinTools is null ? null : new BuiltinToolSession(
                context.BuiltinTools, _webSearch, _webSearchHistory,
                context.ChannelType, context.DefaultTimeout, BuiltinToolSession.OutputTokenBudget(context.Payload));
            if (context.EntryProtocol == context.ChannelType)
            {
                var streamLines = _upstream.StreamJsonAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.DefaultTimeout,
                    context.CancellationToken);
                passThroughResponseCapture = new StreamResponseCapture(context.ChannelType);
                streamWriteMetrics = await context.StreamWriter.WriteLinesAsync(
                    EnsureCompletedStreamEndsWithDone(
                        CapturePassThroughResponse(
                            CaptureStreamLines(
                                streamLines,
                                streamLineCaptures,
                                "upstream",
                                context.CancellationToken),
                            passThroughResponseCapture,
                            context.CancellationToken),
                        streamLineCaptures,
                        "downstream",
                        context.CancellationToken),
                    static line => line.Trim().Length > 0,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);
                ttftMs = streamWriteMetrics.TtftMs;
                passThroughTermination = StreamCaptureTermination.Completed;
                upstreamResponse = passThroughResponseCapture
                    .Complete(passThroughTermination)
                    .Response;
            }
            else
            {
                convertedState = new ConvertedProxyStreamState { UpstreamRequest = upstreamRequest };
                var convertedLines = ConvertRoundsAsync(context, streamLineCaptures, convertedState, tools, context.CancellationToken);
                streamWriteMetrics = await context.StreamWriter.WriteLinesAsync(
                    EnsureCompletedStreamEndsWithDone(
                        CaptureStreamLines(
                            convertedLines,
                            streamLineCaptures,
                            "downstream",
                            context.CancellationToken),
                        streamLineCaptures,
                        "downstream",
                        context.CancellationToken),
                    SseStreamConverter.CountsForTtft,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);
                ttftMs = streamWriteMetrics.TtftMs;

                upstreamRequest = convertedState.UpstreamRequest;
                upstreamResponse = convertedState.UpstreamResponse;
                responsePayload = convertedState.ResponsePayload;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            passThroughTermination = exception is OperationCanceledException && context.CancellationToken.IsCancellationRequested
                ? StreamCaptureTermination.ClientCancelled
                : StreamCaptureTermination.UpstreamError;
            var capturedUpstreamResponse = passThroughResponseCapture?
                .Complete(passThroughTermination)
                .Response;
            if (exception is ProxyException proxyException)
            {
                statusCode = proxyException.StatusCode;
                errorResponse = proxyException.ToResponse();
                upstreamResponse = CombineCapturedAndErrorResponse(
                    capturedUpstreamResponse,
                    UpstreamErrorBody(proxyException))
                    ?? upstreamResponse;
            }
            else
            {
                statusCode = exception is OperationCanceledException
                    ? context.CancellationToken.IsCancellationRequested
                        ? ProxyHttpStatus.ClientClosedRequest
                        : ProxyHttpStatus.GatewayTimeout
                    : ProxyHttpStatus.InternalServerError;
                upstreamResponse ??= capturedUpstreamResponse;
            }
            if (exception is OperationCanceledException && !context.CancellationToken.IsCancellationRequested)
            {
                var timeout = new UpstreamException("proxy request timed out", ProxyHttpStatus.GatewayTimeout);
                error = timeout.Message;
                errorResponse = timeout.ToResponse();
                throw timeout;
            }
            throw;
        }
        finally
        {
            if (convertedState is not null)
            {
                upstreamRequest = convertedState.UpstreamRequest;
                upstreamResponse ??= convertedState.UpstreamResponse;
                responsePayload ??= convertedState.ResponsePayload;
            }
            webSearchDetails = tools?.Details;
            if (passThroughResponseCapture is not null && upstreamResponse is null)
            {
                upstreamResponse = passThroughResponseCapture
                    .Complete(passThroughTermination)
                    .Response;
            }

            await _logs.CompleteLogAsync(
                context.RequestLogId,
                new ProxyLogContext(
                    context.RequestId,
                    context.OwnerUsername,
                    context.ApiKeyId,
                    context.OriginalPayload,
                    upstreamRequest,
                    upstreamResponse,
                    responsePayload,
                    errorResponse,
                    context.RequestModel,
                    context.UpstreamModel,
                    context.ChannelId,
                    context.ChannelType,
                    IsStream: true,
                    TtftMs: ttftMs,
                    StatusCode: statusCode,
                    DurationMs: ElapsedMilliseconds(context.StartedTimestamp),
                    error,
                    webSearchDetails,
                    StreamLines: streamLineCaptures)
                {
                    AggregatedUsage = tools?.HasCalls == true ? tools.AccountingUsage : null
                },
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

    private static Dictionary<string, object?>? CombineCapturedAndErrorResponse(
        Dictionary<string, object?>? captured,
        Dictionary<string, object?>? errorResponse)
    {
        if (captured is null)
        {
            return errorResponse;
        }

        if (errorResponse is null)
        {
            return captured;
        }

        var hasProtocolResponse = captured.Keys.Any(key => key != "_opencodex_capture");
        if (!hasProtocolResponse)
        {
            if (captured.TryGetValue("_opencodex_capture", out var captureMetadata))
            {
                errorResponse["_opencodex_capture"] = captureMetadata;
            }

            return errorResponse;
        }

        foreach (var (key, value) in errorResponse)
        {
            captured.TryAdd(key, value);
        }

        return captured;
    }

    internal static async IAsyncEnumerable<string> CapturePassThroughResponse(
        IAsyncEnumerable<string> lines,
        StreamResponseCapture capture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            capture.Accept(line);
            yield return line;
            if (capture.IsComplete)
            {
                yield break;
            }
        }
    }

    internal static async IAsyncEnumerable<string> CaptureStreamLines(
        IAsyncEnumerable<string> lines,
        IList<ProxyRequestStreamLineCapture> capture,
        string source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            foreach (var rawLine in SplitStreamLogLines(line))
            {
                AddStreamLineCapture(capture, source, rawLine);
            }

            yield return line;
        }
    }

    private static async IAsyncEnumerable<string> EnsureCompletedStreamEndsWithDone(
        IAsyncEnumerable<string> lines,
        IList<ProxyRequestStreamLineCapture> capture,
        string source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sawCompleted = false;
        var sawDone = false;
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            sawCompleted |= line.Contains("response.completed", StringComparison.Ordinal);
            sawDone |= line.Contains("data: [DONE]", StringComparison.Ordinal);
            yield return line;
        }

        if (!sawCompleted || sawDone)
        {
            yield break;
        }

        const string done = "data: [DONE]\n\n";
        foreach (var rawLine in SplitStreamLogLines(done))
        {
            AddStreamLineCapture(capture, source, rawLine);
        }

        yield return done;
    }

    private static IEnumerable<string> SplitStreamLogLines(string chunk)
    {
        var normalized = chunk.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var parts = normalized.Split('\n');
        var count = normalized.EndsWith('\n') ? parts.Length - 1 : parts.Length;
        for (var i = 0; i < count; i++)
        {
            yield return parts[i];
        }
    }


    private static void AddStreamLineCapture(
        IList<ProxyRequestStreamLineCapture> capture,
        string source,
        string rawLine)
    {
        capture.Add(new ProxyRequestStreamLineCapture(
            capture.Count,
            source,
            rawLine));
    }

}

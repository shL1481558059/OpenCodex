using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.Services.WebSearch;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

public sealed partial class WebSearchSimulator
{
    public async IAsyncEnumerable<string> RunChatStreamAsync(
        IReadOnlyDictionary<string, object?> channel,
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> payload,
        string? originalModel,
        int defaultTimeout,
        WebSearchStreamResult result,
        Func<IAsyncEnumerable<string>, string, IAsyncEnumerable<string>>? streamCapture,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var protocol = StringValue(channel, "type");
        var requestPayload = DeepCopyObject(upstreamRequest);
        requestPayload["stream"] = true;
        var webResults = new List<WebSearchToolResult>();
        var upstreamCalls = new List<Dictionary<string, object?>>();
        WebSearchStreamEventState? streamState = null;
        var webExecuted = 0;
        var webLimit = WebSearchRequestPolicy.MaxWebSearchCalls(payload);
        var maxIterations = Math.Max(webLimit + 3, 3);
        var completedLine = (string?)null;
        var completedConverted = (ConvertedStreamResult?)null;
        var completedRequest = requestPayload;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var converted = new ConvertedStreamResult();
            var lines = _upstream.StreamJsonAsync(channel, requestPayload, defaultTimeout, cancellationToken);
            lines = CaptureUpstreamLines(lines, streamCapture);
            var events = new List<string>();
            var convertedLines = protocol == ProtocolConverter.Messages
                ? SseStreamConverter.MessagesToResponsesEvents(
                    lines,
                    originalModel,
                    converted,
                    new HashSet<string>([WebSearchToolName], StringComparer.Ordinal),
                    SkipResponseCreated: streamState is not null,
                    InitialSequenceNumber: streamState?.SequenceNumber ?? 0,
                    InitialOutputIndex: streamState?.NextOutputIndex ?? 0,
                    cancellationToken)
                : SseStreamConverter.ChatToResponsesEvents(
                    lines,
                    originalModel,
                    converted,
                    new HashSet<string>([WebSearchToolName], StringComparer.Ordinal),
                    SkipResponseCreated: streamState is not null,
                    InitialSequenceNumber: streamState?.SequenceNumber ?? 0,
                    InitialOutputIndex: streamState?.NextOutputIndex ?? 0,
                    cancellationToken);
            await foreach (var line in convertedLines.WithCancellation(cancellationToken))
            {
                // 边接收边输出（但跳过completed事件，因为可能还有web_search要执行）
                if (!line.StartsWith("event: response.completed\n", StringComparison.Ordinal))
                {
                    yield return line;
                }
                events.Add(line);
            }

            if (converted.UpstreamResponse is null)
            {
                yield break;
            }

            var toolCalls = WebSearchToolCallParser.ExtractToolCalls(converted.UpstreamResponse, protocol);
            var webCalls = toolCalls
                .Where(call => string.Equals(call.Name, WebSearchToolName, StringComparison.Ordinal))
                .ToList();
            var otherCalls = toolCalls
                .Where(call => !string.Equals(call.Name, WebSearchToolName, StringComparison.Ordinal))
                .ToList();
            upstreamCalls.Add(new Dictionary<string, object?>
            {
                ["iteration"] = iteration + 1,
                ["after_limit"] = false,
                ["tool_calls"] = toolCalls
                    .Select(call => (object?)new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["name"] = call.Name
                    })
                    .ToList()
            });

            var eventPrefix = events
                .Where(line => !line.StartsWith("event: response.completed\n", StringComparison.Ordinal))
                .ToList();
            completedLine = events.FirstOrDefault(line =>
                line.StartsWith("event: response.completed\n", StringComparison.Ordinal));
            completedConverted = converted;
            completedRequest = requestPayload;
            streamState ??= WebSearchStreamEventState.FromEvents(eventPrefix);
            streamState.ObserveEvents(eventPrefix);

            if (webCalls.Count == 0 || otherCalls.Count > 0)
            {
                break;
            }

            var currentResults = new List<WebSearchToolResult>();
            var forceFinalAnswer = false;
            foreach (var webCall in webCalls)
            {
                var (query, parseError) = WebSearchRequestPolicy.ParseQuery(webCall.Arguments);
                yield return streamState.EmitWebSearchAdded(
                    webCall.Id,
                    query ?? string.Empty,
                    out var outputIndex);

                WebSearchToolResult webResult;
                if (parseError is not null)
                {
                    webResult = WebSearchToolResult.Failed(webCall.Id, query ?? string.Empty, parseError);
                }
                else if (webExecuted >= webLimit)
                {
                    webResult = WebSearchToolResult.Failed(
                        webCall.Id,
                        query ?? string.Empty,
                        "已达到 web_search 调用上限，不能继续搜索");
                    forceFinalAnswer = true;
                }
                else
                {
                    var reserved = ReserveTavilyKey();
                    if (reserved is null)
                    {
                        webResult = WebSearchToolResult.Failed(webCall.Id, query ?? string.Empty, "搜索不可用");
                        forceFinalAnswer = true;
                    }
                    else
                    {
                        webExecuted++;
                        var providerResult = await _webSearchClient.SearchAsync(
                            new WebSearchProviderKey(reserved.Provider, reserved.Key),
                            query ?? string.Empty,
                            cancellationToken);
                        webResult = WebSearchToolResult.FromProvider(webCall.Id, query ?? string.Empty, reserved, providerResult);
                        if (!providerResult.Ok)
                        {
                            forceFinalAnswer = true;
                        }
                    }
                }

                currentResults.Add(webResult);
                webResults.Add(webResult);
                yield return streamState.EmitWebSearchDone(outputIndex, webResult);
            }

            requestPayload = WebSearchContinuationRequest.AppendToolResults(
                requestPayload,
                converted.UpstreamResponse,
                protocol,
                currentResults,
                forceFinalAnswer: forceFinalAnswer || currentResults.Any(result => result.Status != "completed"));
            requestPayload["stream"] = true;

            if (!forceFinalAnswer)
            {
                continue;
            }

            converted = new ConvertedStreamResult();
            lines = _upstream.StreamJsonAsync(channel, requestPayload, defaultTimeout, cancellationToken);
            lines = CaptureUpstreamLines(lines, streamCapture);
            events = [];
            convertedLines = protocol == ProtocolConverter.Messages
                ? SseStreamConverter.MessagesToResponsesEvents(
                    lines,
                    originalModel,
                    converted,
                    new HashSet<string>([WebSearchToolName], StringComparer.Ordinal),
                    SkipResponseCreated: true,
                    InitialSequenceNumber: streamState.SequenceNumber,
                    InitialOutputIndex: streamState.NextOutputIndex,
                    cancellationToken)
                : SseStreamConverter.ChatToResponsesEvents(
                    lines,
                    originalModel,
                    converted,
                    new HashSet<string>([WebSearchToolName], StringComparer.Ordinal),
                    SkipResponseCreated: true,
                    InitialSequenceNumber: streamState.SequenceNumber,
                    InitialOutputIndex: streamState.NextOutputIndex,
                    cancellationToken);
            await foreach (var line in convertedLines.WithCancellation(cancellationToken))
            {
                // 边接收边输出（但跳过completed事件）
                if (!line.StartsWith("event: response.completed\n", StringComparison.Ordinal))
                {
                    yield return line;
                }
                events.Add(line);
            }

            if (converted.UpstreamResponse is null)
            {
                yield break;
            }

            toolCalls = WebSearchToolCallParser.ExtractToolCalls(converted.UpstreamResponse, protocol);
            upstreamCalls.Add(new Dictionary<string, object?>
            {
                ["iteration"] = iteration + 2,
                ["after_limit"] = true,
                ["tool_calls"] = toolCalls
                    .Select(call => (object?)new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["name"] = call.Name
                    })
                    .ToList()
            });
            eventPrefix = events
                .Where(line => !line.StartsWith("event: response.completed\n", StringComparison.Ordinal))
                .ToList();
            completedLine = events.FirstOrDefault(line =>
                line.StartsWith("event: response.completed\n", StringComparison.Ordinal));
            completedConverted = converted;
            completedRequest = requestPayload;
            streamState.ObserveEvents(eventPrefix);

            break;
        }

        if (completedLine is not null)
        {
            yield return WebSearchResponsePayload.InjectWebSearchIntoCompleted(completedLine, webResults);
        }

        var finalUpstreamResponse = completedConverted?.UpstreamResponse;

        result.FinalUpstreamRequest = completedRequest;
        result.FinalUpstreamResponse = finalUpstreamResponse;
        result.Details = WebSearchSimulationLog.Build(webResults, upstreamCalls);
        if (finalUpstreamResponse is not null)
        {
            var responsePayload = ProtocolConverter.ConvertResponse(
                finalUpstreamResponse,
                ProtocolConverter.Responses,
                protocol,
                originalModel);
            responsePayload = WebSearchResponsePayload.PrependWebSearchItems(
                responsePayload,
                webResults,
                includeResult: true);
            responsePayload = WebSearchResponsePayload.AddSourceAnnotations(responsePayload, webResults);
            result.ResponsePayload = responsePayload;
        }
    }

    private static IAsyncEnumerable<string> CaptureUpstreamLines(
        IAsyncEnumerable<string> lines,
        Func<IAsyncEnumerable<string>, string, IAsyncEnumerable<string>>? streamCapture)
    {
        return streamCapture is null ? lines : streamCapture(lines, "upstream");
    }
}

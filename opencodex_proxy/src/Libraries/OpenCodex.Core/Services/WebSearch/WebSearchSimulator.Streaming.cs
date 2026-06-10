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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
            var events = new List<string>();
            await foreach (var line in SseStreamConverter.ChatToResponsesEvents(
                lines,
                originalModel,
                converted,
                new HashSet<string>([WebSearchToolName], StringComparer.Ordinal),
                SkipResponseCreated: streamState is not null,
                InitialSequenceNumber: streamState?.SequenceNumber ?? 0,
                InitialOutputIndex: streamState?.NextOutputIndex ?? 0,
                cancellationToken).WithCancellation(cancellationToken))
            {
                events.Add(line);
            }

            if (converted.UpstreamResponse is null)
            {
                foreach (var line in events)
                {
                    yield return line;
                }

                yield break;
            }

            var toolCalls = WebSearchToolCallParser.ExtractChatToolCalls(converted.UpstreamResponse);
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

            foreach (var line in eventPrefix)
            {
                yield return line;
            }

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
                ProtocolConverter.Chat,
                currentResults,
                forceFinalAnswer: forceFinalAnswer || currentResults.Any(result => result.Status != "completed"));
            requestPayload["stream"] = true;

            if (!forceFinalAnswer)
            {
                continue;
            }

            converted = new ConvertedStreamResult();
            lines = _upstream.StreamJsonAsync(channel, requestPayload, defaultTimeout, cancellationToken);
            events = [];
            await foreach (var line in SseStreamConverter.ChatToResponsesEvents(
                lines,
                originalModel,
                converted,
                new HashSet<string>([WebSearchToolName], StringComparer.Ordinal),
                SkipResponseCreated: true,
                InitialSequenceNumber: streamState.SequenceNumber,
                InitialOutputIndex: streamState.NextOutputIndex,
                cancellationToken).WithCancellation(cancellationToken))
            {
                events.Add(line);
            }

            if (converted.UpstreamResponse is null)
            {
                foreach (var line in events)
                {
                    yield return line;
                }

                yield break;
            }

            toolCalls = WebSearchToolCallParser.ExtractChatToolCalls(converted.UpstreamResponse);
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

            foreach (var line in eventPrefix)
            {
                yield return line;
            }

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
                ProtocolConverter.Chat,
                originalModel);
            responsePayload = WebSearchResponsePayload.PrependWebSearchItems(
                responsePayload,
                webResults,
                includeResult: true);
            responsePayload = WebSearchResponsePayload.AddSourceAnnotations(responsePayload, webResults);
            result.ResponsePayload = responsePayload;
        }
    }
}

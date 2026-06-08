using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
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
        var initialConverted = new ConvertedStreamResult();
        var initialLines = _upstream.StreamJsonAsync(channel, requestPayload, defaultTimeout, cancellationToken);
        var initialEvents = new List<string>();
        await foreach (var line in SseStreamConverter.ChatToResponsesEvents(
            initialLines,
            originalModel,
            initialConverted,
            new HashSet<string>([WebSearchToolName], StringComparer.Ordinal),
            SkipResponseCreated: false,
            InitialSequenceNumber: 0,
            InitialOutputIndex: 0,
            cancellationToken).WithCancellation(cancellationToken))
        {
            initialEvents.Add(line);
        }

        if (initialConverted.UpstreamResponse is null)
        {
            foreach (var line in initialEvents)
            {
                yield return line;
            }

            yield break;
        }

        var toolCalls = WebSearchToolCallParser.ExtractChatToolCalls(initialConverted.UpstreamResponse);
        var webCalls = toolCalls
            .Where(call => string.Equals(call.Name, WebSearchToolName, StringComparison.Ordinal))
            .ToList();
        var otherCalls = toolCalls
            .Where(call => !string.Equals(call.Name, WebSearchToolName, StringComparison.Ordinal))
            .ToList();

        if (webCalls.Count == 0 || otherCalls.Count > 0)
        {
            result.FinalUpstreamRequest = requestPayload;
            result.FinalUpstreamResponse = initialConverted.UpstreamResponse;
            result.ResponsePayload = ProtocolConverter.ConvertResponse(
                initialConverted.UpstreamResponse,
                ProtocolConverter.Responses,
                ProtocolConverter.Chat,
                originalModel);
            foreach (var line in initialEvents)
            {
                yield return line;
            }

            yield break;
        }

        var prefixEvents = initialEvents
            .Where(line => !line.StartsWith("event: response.completed\n", StringComparison.Ordinal))
            .ToList();
        var webResults = new List<WebSearchToolResult>();
        var upstreamCalls = new List<Dictionary<string, object?>>
        {
            new(StringComparer.Ordinal)
            {
                ["iteration"] = 1,
                ["tool_calls"] = toolCalls
                    .Select(call => (object?)new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["name"] = call.Name
                    })
                    .ToList()
            }
        };
        var streamState = WebSearchStreamEventState.FromEvents(prefixEvents);
        var webExecuted = 0;
        var webLimit = WebSearchRequestPolicy.MaxWebSearchCalls(payload);

        foreach (var line in prefixEvents)
        {
            yield return line;
        }

        foreach (var webCall in webCalls)
        {
            var itemId = $"ws_{Guid.NewGuid():N}";
            var (query, parseError) = WebSearchRequestPolicy.ParseQuery(webCall.Arguments);
            yield return streamState.EmitWebSearchAdded(
                itemId,
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
                    string.Empty,
                    "已达到 web_search 调用上限，不能继续搜索");
            }
            else
            {
                var reserved = ReserveTavilyKey();
                if (reserved is null)
                {
                    webResult = WebSearchToolResult.Failed(webCall.Id, query ?? string.Empty, "搜索不可用");
                }
                else
                {
                    webExecuted++;
                    var providerResult = await _webSearchClient.SearchAsync(
                        new WebSearchProviderKey(reserved.Provider, reserved.Key),
                        query ?? string.Empty,
                        cancellationToken);
                    webResult = WebSearchToolResult.FromProvider(webCall.Id, query ?? string.Empty, reserved, providerResult);
                }
            }

            webResults.Add(webResult);
            yield return streamState.EmitWebSearchDone(outputIndex, webResult);
        }

        var finalRequest = WebSearchContinuationRequest.AppendToolResults(
            requestPayload,
            initialConverted.UpstreamResponse,
            ProtocolConverter.Chat,
            webResults);
        finalRequest["stream"] = true;

        var finalConverted = new ConvertedStreamResult();
        var finalLines = _upstream.StreamJsonAsync(channel, finalRequest, defaultTimeout, cancellationToken);
        var finalEvents = new List<string>();
        await foreach (var line in SseStreamConverter.ChatToResponsesEvents(
            finalLines,
            originalModel,
            finalConverted,
            SkipToolNames: null,
            SkipResponseCreated: true,
            InitialSequenceNumber: streamState.SequenceNumber,
            InitialOutputIndex: streamState.NextOutputIndex,
            cancellationToken).WithCancellation(cancellationToken))
        {
            finalEvents.Add(line);
        }

        foreach (var line in finalEvents)
        {
            yield return line.StartsWith("event: response.completed\n", StringComparison.Ordinal)
                ? WebSearchResponsePayload.InjectWebSearchIntoCompleted(line, webResults)
                : line;
        }

        var finalUpstreamResponse = finalConverted.UpstreamResponse;
        upstreamCalls.Add(new Dictionary<string, object?>
        {
            ["iteration"] = 2,
            ["after_limit"] = false,
            ["tool_calls"] = finalUpstreamResponse is null
                ? new List<object?>()
                : WebSearchToolCallParser.ExtractChatToolCalls(finalUpstreamResponse)
                    .Select(call => (object?)new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["name"] = call.Name
                    })
                    .ToList()
        });

        result.FinalUpstreamRequest = finalRequest;
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

using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.Services.WebSearch;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

public sealed partial class WebSearchSimulator
{
    public async Task<WebSearchSimulationResult> RunAsync(
        IReadOnlyDictionary<string, object?> channel,
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> payload,
        string? originalModel,
        int defaultTimeout,
        CancellationToken cancellationToken)
    {
        var protocol = StringValue(channel, "type");
        var requestPayload = DeepCopyObject(upstreamRequest);
        requestPayload["stream"] = false;
        var textFormat = ProtocolConverter.ExtractTextFormat(payload);
        var webResults = new List<WebSearchToolResult>();
        var upstreamCalls = new List<Dictionary<string, object?>>();
        var webLimit = WebSearchRequestPolicy.MaxWebSearchCalls(payload);
        var webExecuted = 0;
        var maxIterations = Math.Max(webLimit + 3, 3);
        Dictionary<string, object?> upstreamResponse = [];

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            upstreamResponse = await PostUpstream(
                channel,
                requestPayload,
                defaultTimeout,
                webResults,
                upstreamCalls,
                cancellationToken);
            var toolCalls = WebSearchToolCallParser.ExtractToolCalls(upstreamResponse, protocol);
            var webCalls = toolCalls
                .Where(call => string.Equals(call.Name, WebSearchToolName, StringComparison.Ordinal))
                .ToList();
            var otherCalls = toolCalls
                .Where(call => !string.Equals(call.Name, WebSearchToolName, StringComparison.Ordinal))
                .ToList();
            upstreamCalls.Add(new Dictionary<string, object?>
            {
                ["iteration"] = iteration + 1,
                ["tool_calls"] = toolCalls
                    .Select(call => (object?)new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["name"] = call.Name
                    })
                    .ToList()
            });

            if (webCalls.Count == 0)
            {
                var responsePayload = ProtocolConverter.ConvertResponse(
                    upstreamResponse,
                    ProtocolConverter.Responses,
                    protocol,
                    originalModel,
                    textFormat);
                if (webResults.Count > 0)
                {
                    responsePayload = WebSearchResponsePayload.PrependWebSearchItems(
                        responsePayload,
                        webResults,
                        includeResult: false);
                    responsePayload = WebSearchResponsePayload.AddSourceAnnotations(responsePayload, webResults);
                }

                return new WebSearchSimulationResult(
                    requestPayload,
                    upstreamResponse,
                    responsePayload,
                    WebSearchSimulationLog.Build(webResults, upstreamCalls));
            }

            var currentResults = new List<WebSearchToolResult>();
            var currentRequiresFinalAnswer = false;
            foreach (var toolCall in webCalls)
            {
                if (webExecuted >= webLimit)
                {
                    var limitResult = WebSearchToolResult.Failed(
                        toolCall.Id,
                        string.Empty,
                        "已达到 web_search 调用上限，不能继续搜索");
                    currentResults.Add(limitResult);
                    webResults.Add(limitResult);
                    currentRequiresFinalAnswer = true;
                    continue;
                }

                var (query, parseError) = WebSearchRequestPolicy.ParseQuery(toolCall.Arguments);
                if (parseError is not null)
                {
                    var parseResult = WebSearchToolResult.Failed(toolCall.Id, query ?? string.Empty, parseError);
                    currentResults.Add(parseResult);
                    webResults.Add(parseResult);
                    continue;
                }

                var reserved = ReserveTavilyKey();
                if (reserved is null)
                {
                    var unavailableResult = WebSearchToolResult.Failed(toolCall.Id, query ?? string.Empty, "搜索不可用");
                    currentResults.Add(unavailableResult);
                    webResults.Add(unavailableResult);
                    currentRequiresFinalAnswer = true;
                    continue;
                }

                webExecuted++;
                var providerResult = await _webSearchClient.SearchAsync(
                    new WebSearchProviderKey(reserved.Provider, reserved.Key),
                    query ?? string.Empty,
                    cancellationToken);
                var result = WebSearchToolResult.FromProvider(toolCall.Id, query ?? string.Empty, reserved, providerResult);
                currentResults.Add(result);
                webResults.Add(result);
                if (!providerResult.Ok)
                {
                    currentRequiresFinalAnswer = true;
                }
            }

            if (otherCalls.Count > 0)
            {
                var responsePayload = ProtocolConverter.ConvertResponse(
                    upstreamResponse,
                    ProtocolConverter.Responses,
                    protocol,
                    originalModel,
                    textFormat);
                responsePayload = WebSearchResponsePayload.ReplaceOrPrependWebSearchItems(responsePayload, webResults);
                responsePayload = WebSearchResponsePayload.AddSourceAnnotations(responsePayload, webResults);
                return new WebSearchSimulationResult(
                    requestPayload,
                    upstreamResponse,
                    responsePayload,
                    WebSearchSimulationLog.Build(webResults, upstreamCalls));
            }

            requestPayload = WebSearchContinuationRequest.AppendToolResults(
                requestPayload,
                upstreamResponse,
                protocol,
                currentResults,
                forceFinalAnswer: currentRequiresFinalAnswer);
            if (currentRequiresFinalAnswer)
            {
                upstreamResponse = await PostUpstream(
                    channel,
                    requestPayload,
                    defaultTimeout,
                    webResults,
                    upstreamCalls,
                    cancellationToken);
                upstreamCalls.Add(new Dictionary<string, object?>
                {
                    ["iteration"] = iteration + 2,
                    ["after_limit"] = true,
                    ["tool_calls"] = WebSearchToolCallParser.ExtractToolCalls(upstreamResponse, protocol)
                        .Select(call => (object?)new Dictionary<string, object?>
                        {
                            ["id"] = call.Id,
                            ["name"] = call.Name
                        })
                        .ToList()
                });
                var responsePayload = ProtocolConverter.ConvertResponse(
                    upstreamResponse,
                    ProtocolConverter.Responses,
                    protocol,
                    originalModel,
                    textFormat);
                responsePayload = WebSearchResponsePayload.PrependWebSearchItems(
                    responsePayload,
                    webResults,
                    includeResult: false);
                responsePayload = WebSearchResponsePayload.AddSourceAnnotations(responsePayload, webResults);
                return new WebSearchSimulationResult(
                    requestPayload,
                    upstreamResponse,
                    responsePayload,
                    WebSearchSimulationLog.Build(webResults, upstreamCalls));
            }
        }

        var fallbackResponse = ProtocolConverter.ConvertResponse(
            upstreamResponse,
            ProtocolConverter.Responses,
            protocol,
            originalModel,
            textFormat);
        fallbackResponse = WebSearchResponsePayload.PrependWebSearchItems(
            fallbackResponse,
            webResults,
            includeResult: false);
        fallbackResponse = WebSearchResponsePayload.AddSourceAnnotations(fallbackResponse, webResults);
        var details = WebSearchSimulationLog.Build(webResults, upstreamCalls);
        details["error"] = "web_search simulation stopped after iteration guard";
        return new WebSearchSimulationResult(requestPayload, upstreamResponse, fallbackResponse, details);
    }
}

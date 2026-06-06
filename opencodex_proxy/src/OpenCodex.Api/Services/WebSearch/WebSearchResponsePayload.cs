using System.Text.Json;
using OpenCodex.Api.Services;
using static OpenCodex.Api.Abstractions.WebSearchPayload;

namespace OpenCodex.Api.Services.WebSearch;

internal static class WebSearchResponsePayload
{
    public static Dictionary<string, object?> ReplaceOrPrependWebSearchItems(
        Dictionary<string, object?> responsePayload,
        IReadOnlyList<WebSearchToolResult> webResults)
    {
        var output = ListValue(responsePayload, "output");
        var hasWebSearchFunction = output.Any(item =>
            TryAsObject(item, out var outputItem)
            && string.Equals(StringValue(outputItem, "type"), "function_call", StringComparison.Ordinal)
            && string.Equals(StringValue(outputItem, "name"), WebSearchRequestPolicy.ToolName, StringComparison.Ordinal));
        return hasWebSearchFunction
            ? ReplaceWebSearchFunctionItems(responsePayload, webResults, includeResult: true)
            : PrependWebSearchItems(responsePayload, webResults, includeResult: false);
    }

    public static Dictionary<string, object?> ReplaceWebSearchFunctionItems(
        Dictionary<string, object?> responsePayload,
        IReadOnlyList<WebSearchToolResult> webResults,
        bool includeResult)
    {
        var byCallId = webResults.ToDictionary(result => result.CallId, StringComparer.Ordinal);
        var output = new List<object?>();
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ListValue(responsePayload, "output"))
        {
            if (TryAsObject(item, out var outputItem)
                && string.Equals(StringValue(outputItem, "type"), "function_call", StringComparison.Ordinal)
                && string.Equals(StringValue(outputItem, "name"), WebSearchRequestPolicy.ToolName, StringComparison.Ordinal)
                && byCallId.TryGetValue(StringValue(outputItem, "call_id"), out var result))
            {
                output.Add(BuildWebSearchItem(result, includeResult));
                inserted.Add(result.CallId);
                continue;
            }

            output.Add(item);
        }

        foreach (var result in webResults)
        {
            if (!inserted.Contains(result.CallId))
            {
                output.Insert(0, BuildWebSearchItem(result, includeResult));
            }
        }

        responsePayload["output"] = output;
        return responsePayload;
    }

    public static Dictionary<string, object?> PrependWebSearchItems(
        Dictionary<string, object?> responsePayload,
        IReadOnlyList<WebSearchToolResult> webResults,
        bool includeResult)
    {
        var output = webResults
            .Select(result => (object?)BuildWebSearchItem(result, includeResult))
            .Concat(ListValue(responsePayload, "output"))
            .ToList();
        responsePayload["output"] = output;
        return responsePayload;
    }

    public static Dictionary<string, object?> BuildWebSearchItem(
        WebSearchToolResult result,
        bool includeResult)
    {
        var item = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = result.CallId,
            ["type"] = "web_search_call",
            ["status"] = result.Status == "completed" ? "completed" : "failed",
            ["action"] = new Dictionary<string, object?>
            {
                ["type"] = "search",
                ["query"] = result.Query
            }
        };
        if (includeResult)
        {
            item["opencodex_result"] = DeepCopy(result.OpenCodexResult);
        }

        return item;
    }

    public static string InjectWebSearchIntoCompleted(
        string line,
        IReadOnlyList<WebSearchToolResult> webResults)
    {
        var (eventName, payload) = ParseSseLine(line);
        if (eventName != "response.completed" || payload is null)
        {
            return line;
        }

        var response = ObjectValue(payload, "response");
        var output = ListValue(response, "output");
        response["output"] = webResults
            .Select(result => (object?)BuildWebSearchItem(result, includeResult: true))
            .Concat(output)
            .ToList();
        payload["response"] = response;
        return $"event: response.completed\ndata: {JsonDumps(payload)}\n\n";
    }

    public static (string EventName, Dictionary<string, object?>? Payload) ParseSseLine(string line)
    {
        var eventName = "message";
        var dataLines = new List<string>();
        foreach (var rawLine in line.Split('\n'))
        {
            var current = rawLine.TrimEnd('\r');
            if (current.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = current["event:".Length..].Trim();
            }
            else if (current.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(current["data:".Length..].TrimStart());
            }
        }

        if (dataLines.Count == 0)
        {
            return (eventName, null);
        }

        try
        {
            using var document = JsonDocument.Parse(string.Join("\n", dataLines));
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? (eventName, (Dictionary<string, object?>)FromJsonElement(document.RootElement)!)
                : (eventName, null);
        }
        catch (JsonException)
        {
            return (eventName, null);
        }
    }

    public static Dictionary<string, object?> AddSourceAnnotations(
        Dictionary<string, object?> responsePayload,
        IReadOnlyList<WebSearchToolResult> webResults)
    {
        var sources = AllSources(webResults);
        if (sources.Count == 0)
        {
            return responsePayload;
        }

        var message = FirstMessageItem(responsePayload);
        if (message is null)
        {
            return responsePayload;
        }

        var content = ListValue(message, "content");
        message["content"] = content;
        if (content.Count == 0)
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "output_text",
                ["text"] = string.Empty
            });
        }

        if (!TryAsObject(content[0], out var textBlock))
        {
            return responsePayload;
        }

        var text = StringValue(textBlock, "text");
        var sourceLines = sources
            .Where(source => StringValue(source, "url").Length > 0)
            .Select(source =>
            {
                var url = StringValue(source, "url");
                var title = StringValue(source, "title");
                return $"- {(title.Length > 0 ? title : url)}: {url}";
            })
            .ToList();
        var startOffset = text.Length;
        if (sourceLines.Count > 0)
        {
            var separator = text.Length > 0 ? "\n\n" : string.Empty;
            var sourceText = $"来源:\n{string.Join("\n", sourceLines)}";
            startOffset = text.Length + separator.Length;
            text = $"{text}{separator}{sourceText}";
            textBlock["text"] = text;
        }

        var annotations = ListValue(textBlock, "annotations");
        foreach (var source in sources)
        {
            var url = StringValue(source, "url");
            if (url.Length == 0)
            {
                continue;
            }

            var title = StringValue(source, "title");
            var line = $"- {(title.Length > 0 ? title : url)}: {url}";
            var startIndex = text.IndexOf(line, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                startIndex = Math.Max(0, startOffset);
            }

            annotations.Add(new Dictionary<string, object?>
            {
                ["type"] = "url_citation",
                ["start_index"] = startIndex,
                ["end_index"] = startIndex + line.Length,
                ["url"] = url,
                ["title"] = title.Length > 0 ? title : url
            });
        }

        if (annotations.Count > 0)
        {
            textBlock["annotations"] = annotations;
        }

        return responsePayload;
    }

    private static List<Dictionary<string, object?>> AllSources(IReadOnlyList<WebSearchToolResult> webResults)
    {
        var sources = new List<Dictionary<string, object?>>();
        foreach (var result in webResults)
        {
            if (!TryAsList(GetValue(result.OpenCodexResult, "results"), out var resultItems))
            {
                continue;
            }

            foreach (var item in resultItems)
            {
                if (TryAsObject(item, out var source))
                {
                    sources.Add(source);
                }
            }
        }

        return sources;
    }

    private static Dictionary<string, object?>? FirstMessageItem(Dictionary<string, object?> responsePayload)
    {
        foreach (var item in ListValue(responsePayload, "output"))
        {
            if (TryAsObject(item, out var outputItem)
                && string.Equals(StringValue(outputItem, "type"), "message", StringComparison.Ordinal))
            {
                return outputItem;
            }
        }

        return null;
    }
}

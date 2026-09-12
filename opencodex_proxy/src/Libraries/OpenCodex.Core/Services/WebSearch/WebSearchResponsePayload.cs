using System.Text.Json;
using System.Text;
using OpenCodex.CoreBase.Domain.WebSearch;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

internal static class WebSearchResponsePayload
{
    public static Dictionary<string, object?> ProjectRound(
        Dictionary<string, object?> response,
        IReadOnlyList<WebSearchToolResult> results,
        string internalName,
        bool includeSources)
    {
        var byId = results.ToDictionary(result => result.CallId, StringComparer.Ordinal);
        var output = new List<object?>();
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in ListValue(response, "output"))
        {
            if (TryAsObject(value, out var item) && StringValue(item, "type") == "function_call"
                && StringValue(item, "name") == internalName)
            {
                var callId = StringValue(item, "call_id");
                if (byId.TryGetValue(callId, out var result) && inserted.Add(callId))
                {
                    output.Add(BuildWebSearchItem(result, true, includeSources));
                }
                continue;
            }
            output.Add(DeepCopy(value));
        }
        response["output"] = output;
        return AddSourceAnnotations(response, results);
    }

    public static Dictionary<string, object?> BuildWebSearchItem(
        WebSearchToolResult result,
        bool includeResult,
        bool includeSources = false)
    {
        var item = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = result.ItemId,
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
        if (includeSources)
        {
            ObjectValue(item, "action")["sources"] = AllSources([result])
                .Select(source => (object?)new Dictionary<string, object?>
                {
                    ["type"] = "url",
                    ["url"] = StringValue(source, "url"),
                    ["title"] = StringValue(source, "title")
                }).ToList();
        }

        return item;
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

        foreach (var message in ListValue(responsePayload, "output").OfType<Dictionary<string, object?>>())
        foreach (var block in ListValue(message, "content").OfType<Dictionary<string, object?>>())
        {
            AnnotateTextBlock(block, sources);
        }
        return responsePayload;
    }

    public static void AnnotateTextBlock(
        Dictionary<string, object?> block,
        IReadOnlyList<WebSearchToolResult> results) => AnnotateTextBlock(block, AllSources(results));

    private static void AnnotateTextBlock(Dictionary<string, object?> block, List<Dictionary<string, object?>> sources)
    {
        if (StringValue(block, "type") != "output_text")
        {
            return;
        }
        var text = StringValue(block, "text");
        var annotations = ListValue(block, "annotations");
        foreach (var source in sources)
        {
            var url = StringValue(source, "url");
            var offset = text.IndexOf(url, StringComparison.Ordinal);
            if (offset < 0 || annotations.OfType<Dictionary<string, object?>>()
                .Any(annotation => StringValue(annotation, "url") == url))
            {
                continue;
            }
            annotations.Add(new Dictionary<string, object?>
            {
                ["type"] = "url_citation",
                ["start_index"] = text[..offset].EnumerateRunes().Count(),
                ["end_index"] = text[..(offset + url.Length)].EnumerateRunes().Count(),
                ["url"] = url,
                ["title"] = StringValue(source, "title", url)
            });
        }
        if (annotations.Count > 0)
        {
            block["annotations"] = annotations;
        }
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
                if (TryAsObject(item, out var source)
                    && Uri.TryCreate(StringValue(source, "url"), UriKind.Absolute, out var uri)
                    && uri.Scheme is "https" or "http"
                    && !sources.Any(existing => StringValue(existing, "url") == StringValue(source, "url")))
                {
                    sources.Add(source);
                }
            }
        }

        return sources;
    }

}

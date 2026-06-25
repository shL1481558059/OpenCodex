using System.Text.Json;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

public static class WebSearchRequestPolicy
{
    public const string ToolName = "web_search";

    private const int DefaultMaxWebSearchCalls = 15;

    public static bool DeclaresWebSearchTool(IReadOnlyDictionary<string, object?> payload)
    {
        if (!TryAsList(GetValue(payload, "tools"), out var tools))
        {
            return false;
        }

        return tools.Any(item =>
            TryAsObject(item, out var tool)
            && string.Equals(StringValue(tool, "type"), ToolName, StringComparison.Ordinal));
    }

    public static int MaxWebSearchCalls(IReadOnlyDictionary<string, object?> payload)
    {
        var value = GetValue(payload, "max_tool_calls");
        if (value is bool)
        {
            return DefaultMaxWebSearchCalls;
        }

        return Math.Max(0, ToInt(value, DefaultMaxWebSearchCalls));
    }

    public static (string? Query, string? Error) ParseQuery(string arguments)
    {
        Dictionary<string, object?> value;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(arguments) ? "{}" : arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "web_search arguments must be an object");
            }

            value = (Dictionary<string, object?>)FromJsonElement(document.RootElement)!;
        }
        catch (JsonException)
        {
            return (null, "web_search arguments must be valid JSON");
        }

        var extraKeys = value.Keys
            .Where(key => !string.Equals(key, "query", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (extraKeys.Count > 0)
        {
            return (null, "web_search only supports the query argument");
        }

        var query = StringValue(value, "query").Trim();
        return query.Length == 0
            ? (null, "web_search query is required")
            : (query, null);
    }

}

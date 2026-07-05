using System.Text.Json;
using OpenCodex.CoreBase.Domain.WebSearch;
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

    public static Dictionary<string, object?> ApplyMode(
        IReadOnlyDictionary<string, object?> payload,
        string mode)
    {
        if (mode != WebSearchModes.Disabled)
        {
            return DeepCopyObject(payload);
        }

        var result = DeepCopyObject(payload);
        DropWebSearchTools(result);
        DropWebSearchToolChoice(result);
        DropWebSearchIncludeItems(result);
        return result;
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

    private static void DropWebSearchTools(Dictionary<string, object?> payload)
    {
        if (!TryAsList(GetValue(payload, "tools"), out var tools))
        {
            return;
        }

        var filtered = tools
            .Where(item => !IsWebSearchTool(item))
            .ToList();
        if (filtered.Count == tools.Count)
        {
            return;
        }

        if (filtered.Count == 0)
        {
            payload.Remove("tools");
            return;
        }

        payload["tools"] = filtered;
    }

    private static void DropWebSearchToolChoice(Dictionary<string, object?> payload)
    {
        var toolChoice = GetValue(payload, "tool_choice");
        if (!IsWebSearchToolChoice(toolChoice))
        {
            return;
        }

        payload.Remove("tool_choice");
    }

    private static void DropWebSearchIncludeItems(Dictionary<string, object?> payload)
    {
        if (!TryAsList(GetValue(payload, "include"), out var includeItems))
        {
            return;
        }

        var filtered = includeItems
            .Where(item => item is not string text || !text.Contains(ToolName, StringComparison.Ordinal))
            .ToList();
        if (filtered.Count == includeItems.Count)
        {
            return;
        }

        if (filtered.Count == 0)
        {
            payload.Remove("include");
            return;
        }

        payload["include"] = filtered;
    }

    private static bool IsWebSearchTool(object? item)
    {
        if (!TryAsObject(item, out var tool))
        {
            return false;
        }

        if (string.Equals(StringValue(tool, "type"), ToolName, StringComparison.Ordinal)
            || string.Equals(StringValue(tool, "name"), ToolName, StringComparison.Ordinal))
        {
            return true;
        }

        return TryAsObject(GetValue(tool, "function"), out var function)
            && string.Equals(StringValue(function, "name"), ToolName, StringComparison.Ordinal);
    }

    private static bool IsWebSearchToolChoice(object? toolChoice)
    {
        if (toolChoice is string text)
        {
            return string.Equals(text.Trim(), ToolName, StringComparison.Ordinal);
        }

        return IsWebSearchTool(toolChoice);
    }
}

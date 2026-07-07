using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private const string WebSearchToolName = "web_search";

    internal static bool IsWebSearchName(string? name)
    {
        return string.Equals(name, WebSearchToolName, StringComparison.Ordinal);
    }

    internal static Dictionary<string, object?> ResponsesWebSearchCallStartedItem(
        object? callId,
        string itemId)
    {
        return Obj(
            ("id", itemId),
            ("type", "web_search_call"),
            ("status", "in_progress"));
    }

    internal static Dictionary<string, object?> ResponsesWebSearchCallItem(
        object? callId,
        object? arguments,
        string? itemId = null)
    {
        return Obj(
            ("id", itemId ?? Convert.ToString(callId) ?? NewId("ws")),
            ("type", "web_search_call"),
            ("status", "completed"),
            ("action", Obj(
                ("type", "search"),
                ("query", WebSearchQueryFromArguments(arguments)))));
    }

    internal static bool IsWebSearchToolChoice(Dictionary<string, object?> toolChoiceObject)
    {
        var type = GetString(toolChoiceObject, "type") ?? string.Empty;
        if (IsWebSearchName(type))
        {
            return true;
        }

        if (type != "function")
        {
            return false;
        }

        return IsWebSearchName(GetString(ObjectValue(toolChoiceObject, "function"), "name"));
    }

    private static string WebSearchQueryFromArguments(object? arguments)
    {
        if (arguments is null)
        {
            return string.Empty;
        }

        if (TryAsObject(arguments, out var argumentObject))
        {
            return GetString(argumentObject, "query") ?? string.Empty;
        }

        var text = Convert.ToString(arguments) ?? string.Empty;
        if (text.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("query", out var query)
                && query.ValueKind == JsonValueKind.String)
            {
                return query.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            return text;
        }

        return text;
    }
}

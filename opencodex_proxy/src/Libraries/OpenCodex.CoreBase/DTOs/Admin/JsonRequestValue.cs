using System.Text.Json;

namespace OpenCodex.CoreBase.DTOs.Admin;

public static class JsonRequestValue
{
    public static Dictionary<string, object?> Object(
        IReadOnlyDictionary<string, object?>? source)
    {
        if (source is null)
        {
            return [];
        }

        return source.ToDictionary(
            pair => pair.Key,
            pair => Value(pair.Value),
            StringComparer.Ordinal);
    }

    public static List<object?> List(IEnumerable<object?>? source)
    {
        return source?.Select(Value).ToList() ?? [];
    }

    public static object? Value(object? value)
    {
        return value switch
        {
            JsonElement element => FromJsonElement(element),
            IReadOnlyDictionary<string, object?> dictionary => Object(dictionary),
            IEnumerable<object?> values => values.Select(Value).ToList(),
            _ => value
        };
    }

    private static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => FromJsonElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => NumberValue(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static object NumberValue(JsonElement element)
    {
        if (!element.TryGetInt64(out var longValue))
        {
            return element.GetDouble();
        }

        if (longValue is >= int.MinValue and <= int.MaxValue)
        {
            return (int)longValue;
        }

        return longValue;
    }
}

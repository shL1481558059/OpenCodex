using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenCodex.CoreBase.Abstractions;

public static class WebSearchPayload
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => FromJsonElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue is >= int.MinValue and <= int.MaxValue ? (int)longValue : longValue
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public static Dictionary<string, object?> DeepCopyObject(IReadOnlyDictionary<string, object?> value)
    {
        return value.ToDictionary(pair => pair.Key, pair => DeepCopy(pair.Value), StringComparer.Ordinal);
    }

    public static object? DeepCopy(object? value)
    {
        return value switch
        {
            Dictionary<string, object?> dictionary => DeepCopyObject(dictionary),
            IReadOnlyDictionary<string, object?> dictionary => DeepCopyObject(dictionary),
            List<object?> list => list.Select(DeepCopy).ToList(),
            IReadOnlyList<object?> list => list.Select(DeepCopy).ToList(),
            _ => value
        };
    }

    public static Dictionary<string, object?> ObjectValue(Dictionary<string, object?> dictionary, string key)
    {
        return TryAsObject(GetValue(dictionary, key), out var result) ? result : [];
    }

    public static Dictionary<string, object?>? FirstObject(List<object?> list)
    {
        foreach (var item in list)
        {
            if (TryAsObject(item, out var result))
            {
                return result;
            }
        }

        return null;
    }

    public static List<object?> ListValue(Dictionary<string, object?> dictionary, string key)
    {
        return TryAsList(GetValue(dictionary, key), out var result) ? result : [];
    }

    public static bool TryAsObject(object? value, out Dictionary<string, object?> dictionary)
    {
        if (value is Dictionary<string, object?> typed)
        {
            dictionary = typed;
            return true;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            dictionary = readOnly.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return true;
        }

        dictionary = [];
        return false;
    }

    public static bool TryAsList(object? value, out List<object?> list)
    {
        if (value is List<object?> typed)
        {
            list = typed;
            return true;
        }

        if (value is IReadOnlyList<object?> readOnly)
        {
            list = readOnly.ToList();
            return true;
        }

        list = [];
        return false;
    }

    public static object? GetValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : null;
    }

    public static string StringValue(IReadOnlyDictionary<string, object?> dictionary, string key, string fallback = "")
    {
        return GetValue(dictionary, key)?.ToString() ?? fallback;
    }

    public static string JsonDumps(object? value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    public static int ToInt(object? value, int fallback)
    {
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            double doubleValue => (int)doubleValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => fallback
        };
    }
}

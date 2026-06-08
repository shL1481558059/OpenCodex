using System.Collections;
using System.Text.Json;
using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static string JsonDumps(object? value)
    {
        if (value is string text)
        {
            return text;
        }

        return JsonSerializer.Serialize(NormalizeJsonValue(value), JsonOptions);
    }

    private static Dictionary<string, object?> ParseJsonObject(object? value)
    {
        if (TryAsObject(value, out var dictionary))
        {
            return dictionary;
        }

        if (value is not string text)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var parsed = FromJsonElement(document.RootElement);
            return TryAsObject(parsed, out var parsedObject) ? parsedObject : Obj(("input", parsed));
        }
        catch (JsonException)
        {
            return Obj(("input", text));
        }
    }

    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] values)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            result[key] = value;
        }

        return result;
    }

    private static object? GetValue(Dictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? NormalizeJsonValue(value) : null;
    }

    private static string? GetString(Dictionary<string, object?> dictionary, string key)
    {
        return GetValue(dictionary, key) as string;
    }

    private static bool HasNonNullValue(Dictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) && NormalizeJsonValue(value) is not null;
    }

    private static void MergeInto(Dictionary<string, object?> target, Dictionary<string, object?> source)
    {
        foreach (var (key, value) in source)
        {
            target[key] = DeepCopy(value);
        }
    }

    private static Dictionary<string, object?> ObjectValue(Dictionary<string, object?> dictionary, string key)
    {
        return TryAsObject(GetValue(dictionary, key), out var value)
            ? value
            : new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static List<object?> ListValue(Dictionary<string, object?> dictionary, string key)
    {
        return TryAsList(GetValue(dictionary, key), out var value) ? value : [];
    }

    private static List<object?> AsOptionalList(object? value)
    {
        return TryAsList(value, out var list) ? list : [];
    }

    private static Dictionary<string, object?>? FirstObject(List<object?> values)
    {
        foreach (var value in values)
        {
            if (TryAsObject(value, out var dictionary))
            {
                return dictionary;
            }
        }

        return null;
    }

    private static Dictionary<string, object?> AsObject(object? value)
    {
        if (TryAsObject(value, out var dictionary))
        {
            return dictionary;
        }

        throw new BadRequestException("expected object");
    }

    private static bool TryAsObject(object? value, out Dictionary<string, object?> dictionary)
    {
        value = NormalizeJsonValue(value);
        if (value is Dictionary<string, object?> typedDictionary)
        {
            dictionary = typedDictionary;
            return true;
        }

        if (value is IDictionary<string, object?> genericDictionary)
        {
            dictionary = genericDictionary.ToDictionary(
                pair => pair.Key,
                pair => NormalizeJsonValue(pair.Value),
                StringComparer.Ordinal);
            return true;
        }

        if (value is IDictionary nonGenericDictionary)
        {
            dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in nonGenericDictionary)
            {
                if (entry.Key is string key)
                {
                    dictionary[key] = NormalizeJsonValue(entry.Value);
                }
            }

            return true;
        }

        dictionary = [];
        return false;
    }

    private static bool TryAsList(object? value, out List<object?> list)
    {
        value = NormalizeJsonValue(value);
        if (value is List<object?> typedList)
        {
            list = typedList;
            return true;
        }

        if (value is IList<object?> genericList)
        {
            list = genericList.Select(NormalizeJsonValue).ToList();
            return true;
        }

        if (value is IEnumerable enumerable and not string and not IDictionary)
        {
            list = [];
            foreach (var item in enumerable)
            {
                list.Add(NormalizeJsonValue(item));
            }

            return true;
        }

        list = [];
        return false;
    }

    private static object? DeepCopy(object? value)
    {
        value = NormalizeJsonValue(value);
        if (TryAsObject(value, out var dictionary))
        {
            return dictionary.ToDictionary(
                pair => pair.Key,
                pair => DeepCopy(pair.Value),
                StringComparer.Ordinal);
        }

        if (TryAsList(value, out var list))
        {
            return list.Select(DeepCopy).ToList();
        }

        return value;
    }

    private static object? NormalizeJsonValue(object? value)
    {
        return value switch
        {
            JsonElement element => FromJsonElement(element),
            JsonDocument document => FromJsonElement(document.RootElement),
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
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue is >= int.MinValue and <= int.MaxValue ? (object)(int)longValue : longValue
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static bool IsTruthy(object? value)
    {
        value = NormalizeJsonValue(value);
        return value switch
        {
            null => false,
            bool boolValue => boolValue,
            string text => text.Length > 0,
            _ when TryAsList(value, out var list) => list.Count > 0,
            _ when TryAsObject(value, out var dictionary) => dictionary.Count > 0,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            double doubleValue => Math.Abs(doubleValue) > double.Epsilon,
            _ => true
        };
    }

    private static int ToInt(object? value)
    {
        value = NormalizeJsonValue(value);
        return value switch
        {
            null => 0,
            int intValue => intValue,
            long longValue => checked((int)longValue),
            double doubleValue => (int)doubleValue,
            decimal decimalValue => (int)decimalValue,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => 0
        };
    }

    private static long Now()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static string NewId(string prefix)
    {
        return $"{prefix}_{Guid.NewGuid():N}";
    }
}

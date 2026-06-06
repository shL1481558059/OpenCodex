using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static Dictionary<string, object?> ParseJsonObject(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return FromJsonElement(document.RootElement) as Dictionary<string, object?>
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static List<object?> ParseJsonList(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return FromJsonElement(document.RootElement) as List<object?> ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
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
                ? longValue
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static string JsonDumps(object? value)
    {
        return JsonSerializer.Serialize(NormalizeJsonValue(value), JsonOptions);
    }

    private static object? NormalizeJsonValue(object? value)
    {
        if (value is JsonElement element)
        {
            return FromJsonElement(element);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            return readOnlyDictionary.ToDictionary(
                pair => pair.Key,
                pair => NormalizeJsonValue(pair.Value),
                StringComparer.Ordinal);
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            return dictionary.ToDictionary(
                pair => pair.Key,
                pair => NormalizeJsonValue(pair.Value),
                StringComparer.Ordinal);
        }

        if (value is IDictionary nonGenericDictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in nonGenericDictionary)
            {
                if (entry.Key is string key)
                {
                    result[key] = NormalizeJsonValue(entry.Value);
                }
            }

            return result;
        }

        if (value is string)
        {
            return value;
        }

        if (value is IEnumerable<object?> genericEnumerable)
        {
            return genericEnumerable.Select(NormalizeJsonValue).ToList();
        }

        if (value is IEnumerable enumerable)
        {
            var result = new List<object?>();
            foreach (var item in enumerable)
            {
                result.Add(NormalizeJsonValue(item));
            }

            return result;
        }

        return value;
    }
}

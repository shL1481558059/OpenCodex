using System.Collections;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private static object? GetOptionalValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : null;
    }

    private static string? OptionalNullableString(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        var value = GetOptionalValue(dictionary, key);
        return value is null ? null : value.ToString();
    }

    private static int OptionalInt(IReadOnlyDictionary<string, object?> dictionary, string key, int defaultValue)
    {
        var value = GetOptionalValue(dictionary, key);
        return value is null ? defaultValue : ToInt(value);
    }

    private static double OptionalDouble(IReadOnlyDictionary<string, object?> dictionary, string key, double defaultValue)
    {
        var value = GetOptionalValue(dictionary, key);
        if (value is null)
        {
            return defaultValue;
        }

        try
        {
            return Convert.ToDouble(value);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return defaultValue;
        }
    }

    private static int? OptionalNullableInt(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        var value = GetOptionalValue(dictionary, key);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static long? OptionalNullableLong(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        var value = GetOptionalValue(dictionary, key);
        if (value is null)
        {
            return null;
        }

        return TryConvertInt64(value, out var parsed) ? parsed : null;
    }

    private static double? OptionalNullableDouble(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        var value = GetOptionalValue(dictionary, key);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(value);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static int ToInt(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static bool TryConvertInt64(object? value, out long parsed)
    {
        try
        {
            parsed = Convert.ToInt64(value);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            parsed = 0;
            return false;
        }
    }

    private static bool TryConvertInt32(object? value, out int parsed)
    {
        try
        {
            parsed = Convert.ToInt32(value);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            parsed = 0;
            return false;
        }
    }

    private static bool TryConvertDouble(object? value, out double parsed)
    {
        try
        {
            parsed = Convert.ToDouble(value);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            parsed = 0;
            return false;
        }
    }

    private static bool TryAsObject(object? value, out Dictionary<string, object?> dictionary)
    {
        if (value is Dictionary<string, object?> typedDictionary)
        {
            dictionary = typedDictionary;
            return true;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            dictionary = readOnlyDictionary.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            return true;
        }

        if (value is IDictionary<string, object?> genericDictionary)
        {
            dictionary = genericDictionary.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
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
                    dictionary[key] = entry.Value;
                }
            }

            return true;
        }

        dictionary = [];
        return false;
    }

    private static bool TryAsList(object? value, out List<object?> list)
    {
        if (value is List<object?> typedList)
        {
            list = typedList;
            return true;
        }

        if (value is IList<object?> genericList)
        {
            list = genericList.ToList();
            return true;
        }

        if (value is IEnumerable enumerable and not string and not IDictionary)
        {
            list = [];
            foreach (var item in enumerable)
            {
                list.Add(item);
            }

            return true;
        }

        list = [];
        return false;
    }

    private static string RequiredString(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        if (!dictionary.TryGetValue(key, out var value))
        {
            throw new KeyNotFoundException(key);
        }

        return value?.ToString() ?? string.Empty;
    }

    private static string OptionalString(IReadOnlyDictionary<string, object?> dictionary, string key, string defaultValue)
    {
        var value = GetOptionalValue(dictionary, key);
        if (IsPythonFalsy(value))
        {
            return defaultValue;
        }

        return value?.ToString() ?? defaultValue;
    }
}

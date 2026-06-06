using System.Collections;

namespace OpenCodex.Api.Config;

internal static class ConfigValue
{
    public static Dictionary<string, object?> Object(params (string Key, object? Value)[] values)
    {
        return values.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
    }

    public static Dictionary<string, object?> AsObject(object? value)
    {
        if (value is Dictionary<string, object?> typedDictionary)
        {
            return typedDictionary;
        }

        if (value is IDictionary<string, object?> genericDictionary)
        {
            return genericDictionary.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
        }

        if (value is IDictionary nonGenericDictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in nonGenericDictionary)
            {
                if (entry.Key is string key)
                {
                    result[key] = entry.Value;
                }
            }

            return result;
        }

        throw new ConfigException("expected object");
    }

    public static bool TryAsObject(object? value, out Dictionary<string, object?> dictionary)
    {
        try
        {
            dictionary = AsObject(value);
            return true;
        }
        catch (ConfigException)
        {
            dictionary = [];
            return false;
        }
    }

    public static List<object?> AsList(object? value)
    {
        if (value is List<object?> list)
        {
            return list;
        }

        if (value is IList<object?> genericList)
        {
            return genericList.ToList();
        }

        if (value is IEnumerable enumerable and not string and not IDictionary)
        {
            var result = new List<object?>();
            foreach (var item in enumerable)
            {
                result.Add(item);
            }

            return result;
        }

        throw new ConfigException("expected list");
    }

    public static bool TryAsList(object? value, out List<object?> list)
    {
        try
        {
            list = AsList(value);
            return true;
        }
        catch (ConfigException)
        {
            list = [];
            return false;
        }
    }

    public static string PythonString(object? value)
    {
        return value switch
        {
            null => "None",
            bool boolValue => boolValue ? "True" : "False",
            _ => value.ToString() ?? string.Empty
        };
    }

    public static object? DeepCopy(object? value)
    {
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

    public static string PythonList(IEnumerable<string> values)
    {
        return $"[{string.Join(", ", values.Select(value => $"'{value}'"))}]";
    }
}

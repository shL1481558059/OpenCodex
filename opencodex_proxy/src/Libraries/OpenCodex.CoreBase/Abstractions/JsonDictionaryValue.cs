namespace OpenCodex.CoreBase.Abstractions;

public static class JsonDictionaryValue
{
    public static object? Get(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : null;
    }

    public static string String(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return (Get(dictionary, key)?.ToString() ?? string.Empty).Trim();
    }

    public static List<object?> List(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return Get(dictionary, key) switch
        {
            List<object?> list => list,
            IEnumerable<object?> values => values.ToList(),
            _ => []
        };
    }

    public static Dictionary<string, object?> Object(
        IReadOnlyDictionary<string, object?> dictionary,
        string key,
        Func<IReadOnlyDictionary<string, object?>, Dictionary<string, object?>> clone)
    {
        return Get(dictionary, key) is IReadOnlyDictionary<string, object?> value
            ? clone(value)
            : [];
    }
}

namespace OpenCodex.Core.Services;

public sealed partial class ChannelDiagnosticsService
{
    private static int ToInt(object? value, int defaultValue)
    {
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            double doubleValue => (int)doubleValue,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static Dictionary<string, object?> CloneObject(IReadOnlyDictionary<string, object?> source)
    {
        return source.ToDictionary(
            pair => pair.Key,
            pair => CloneJsonValue(pair.Value),
            StringComparer.Ordinal);
    }

    private static object? CloneJsonValue(object? value)
    {
        return value switch
        {
            IReadOnlyDictionary<string, object?> dictionary => CloneObject(dictionary),
            IReadOnlyList<object?> list => list.Select(CloneJsonValue).ToList(),
            _ => value
        };
    }
}

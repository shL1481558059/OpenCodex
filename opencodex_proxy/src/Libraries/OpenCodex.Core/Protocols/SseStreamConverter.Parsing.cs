using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    public static async IAsyncEnumerable<SseEvent> ParseEvents(
        IAsyncEnumerable<string> lines,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in SseFrameReader.ReadLines(lines, cancellationToken))
            if (frame.Event is { } decoded) yield return decoded;
    }

    public static bool CountsForTtft(string line)
    {
        return line.Contains("response.output_text.delta", StringComparison.Ordinal)
            || line.Contains("response.reasoning_summary_text.delta", StringComparison.Ordinal)
            || line.Contains("response.function_call_arguments.delta", StringComparison.Ordinal)
            || line.Contains("response.custom_tool_call_input.delta", StringComparison.Ordinal)
            || line.Contains("response.output_item.done", StringComparison.Ordinal);
    }

    // ASCII marker matching over frozen generated values has exactly the old substring
    // behavior (including markers inside content/keys), without encoding a long event.
    // Raw wire, comments and malformed input retain the original byte/text semantics.
    internal static Func<string,bool> FrameCountsForTtft { get; }=SseFramePredicate.Create(CountsForTtft,
        static frame=>frame.IsGenerated
            ?CountsForTtft(frame.Event!.EventName)||ContainsTtftMarker(frame.Event.Data)
            :CountsForTtft(frame.WireText));
    private static bool ContainsTtftMarker(object? value)=>value switch
    {
        string text=>CountsForTtft(text),
        IReadOnlyDictionary<string,object?> map=>map.Any(pair=>CountsForTtft(pair.Key)||ContainsTtftMarker(pair.Value)),
        IReadOnlyList<object?> items=>items.Any(ContainsTtftMarker),
        _=>false
    };

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
                ? longValue is >= int.MinValue and <= int.MaxValue ? (int)longValue : longValue
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool TryAsObject(object? value, out Dictionary<string, object?> dictionary)
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

    private static bool TryAsList(object? value, out List<object?> list)
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

    private static object? GetValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : null;
    }

    private static string StringValue(IReadOnlyDictionary<string, object?> dictionary, string key, string? fallback)
    {
        var value = GetValue(dictionary, key);
        return value is null ? fallback ?? string.Empty : value.ToString() ?? fallback ?? string.Empty;
    }

    private static int ToInt(object? value)
    {
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            double doubleValue => (int)doubleValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => 0
        };
    }
}

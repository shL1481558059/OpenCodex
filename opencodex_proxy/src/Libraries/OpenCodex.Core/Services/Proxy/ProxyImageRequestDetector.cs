using OpenCodex.Core.Protocols;

namespace OpenCodex.Core.Services.Proxy;

public static class ProxyImageRequestDetector
{
    public static bool ContainsImageInput(
        IReadOnlyDictionary<string, object?> payload,
        string entryProtocol)
    {
        return entryProtocol switch
        {
            ProtocolConverter.Responses => ResponsesContainsImage(payload),
            ProtocolConverter.Chat => ChatContainsImage(payload),
            ProtocolConverter.Messages => MessagesContainsImage(payload),
            _ => false
        };
    }

    private static bool ResponsesContainsImage(IReadOnlyDictionary<string, object?> payload)
    {
        foreach (var item in ListValue(payload, "input"))
        {
            if (!TryAsObject(item, out var inputItem))
            {
                continue;
            }

            var type = StringValue(inputItem, "type");
            if (type == "message" && ContentContainsType(inputItem, "content", "input_image"))
            {
                return true;
            }

            if (type == "function_call_output" && ContentContainsType(inputItem, "output", "input_image"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ChatContainsImage(IReadOnlyDictionary<string, object?> payload)
    {
        foreach (var item in ListValue(payload, "messages"))
        {
            if (TryAsObject(item, out var message)
                && ContentContainsType(message, "content", "image_url"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MessagesContainsImage(IReadOnlyDictionary<string, object?> payload)
    {
        foreach (var item in ListValue(payload, "messages"))
        {
            if (TryAsObject(item, out var message)
                && ContentContainsType(message, "content", "image"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContentContainsType(
        IReadOnlyDictionary<string, object?> source,
        string key,
        string expectedType)
    {
        foreach (var item in ListValue(source, key))
        {
            if (TryAsObject(item, out var part)
                && StringValue(part, "type") == expectedType)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<object?> ListValue(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        if (!source.TryGetValue(key, out var value))
        {
            return [];
        }

        return value switch
        {
            IReadOnlyList<object?> list => list,
            IEnumerable<object?> values => values.ToList(),
            _ => []
        };
    }

    private static bool TryAsObject(
        object? value,
        out IReadOnlyDictionary<string, object?> result)
    {
        if (value is IReadOnlyDictionary<string, object?> dictionary)
        {
            result = dictionary;
            return true;
        }

        result = new Dictionary<string, object?>(StringComparer.Ordinal);
        return false;
    }

    private static string StringValue(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        return source.TryGetValue(key, out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;
    }
}

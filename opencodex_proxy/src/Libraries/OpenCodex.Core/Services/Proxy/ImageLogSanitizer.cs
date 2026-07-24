using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace OpenCodex.Core.Services.Proxy;

internal static class ImageLogSanitizer
{
    internal const string RedactedValue = "***REDACTED***";
    internal const string RedactedImageValue = "***IMAGE_DATA_REDACTED***";
    internal const string RedactedBinaryValue = "***BINARY_DATA_REDACTED***";

    private static readonly HashSet<string> SensitiveLogKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "authorization_token", "api-key", "api_key", "apikey", "x-api-key",
        "cookie", "set-cookie", "password", "access_token", "refresh_token"
    };

    internal static object? CopyAndSanitize(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement element) return CopyJsonElement(element);
        if (value is byte[] bytes) return $"{RedactedBinaryValue} ({bytes.Length} bytes)";
        if (value is Stream stream) return $"{RedactedBinaryValue} ({stream.GetType().Name})";
        if (value is string text) return SanitizeString(text);
        if (value is IDictionary dictionary) return CopyDictionary(dictionary);

        if (value is IEnumerable enumerable)
        {
            var items = enumerable.Cast<object?>().ToList();
            if (items.Count > 0 && items.All(IsKeyValuePair))
            {
                return items.ToDictionary(
                    item => (string?)item?.GetType().GetProperty("Key")?.GetValue(item) ?? string.Empty,
                    item => SanitizeProperty(
                        (string?)item?.GetType().GetProperty("Key")?.GetValue(item) ?? string.Empty,
                        item?.GetType().GetProperty("Value")?.GetValue(item)),
                    StringComparer.Ordinal);
            }
            return items.Select(CopyAndSanitize).ToList();
        }

        return value;
    }

    private static Dictionary<string, object?> CopyDictionary(IDictionary dictionary)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            result[key] = SanitizeProperty(key, entry.Value);
        }
        return result;
    }

    private static object? CopyJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => SanitizeProperty(property.Name, property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(CopyJsonElement).ToList(),
            JsonValueKind.String => SanitizeString(element.GetString()),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static object? SanitizeProperty(string key, object? value)
    {
        if (SensitiveLogKeys.Contains(key)) return RedactedValue;
        if (key.Equals("b64_json", StringComparison.OrdinalIgnoreCase)) return RedactedImageValue;
        return CopyAndSanitize(value);
    }

    private static string? SanitizeString(string? value)
    {
        return value?.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) == true
            ? RedactedImageValue
            : value;
    }

    private static bool IsKeyValuePair(object? value)
    {
        var type = value?.GetType();
        return type?.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance) is not null
            && type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance) is not null;
    }
}

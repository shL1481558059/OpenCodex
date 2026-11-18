using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    private static Dictionary<string, object?> MessagesUsageToResponsesUsage(
        IReadOnlyDictionary<string, object?> usage)
    {
        var inputTokens = ToInt(GetValue(usage, "input_tokens"));
        var outputTokens = ToInt(GetValue(usage, "output_tokens"));
        var cacheCreation = ToInt(GetValue(usage, "cache_creation_input_tokens"));
        var cacheRead = ToInt(GetValue(usage, "cache_read_input_tokens"));
        var cachedTokens = cacheCreation + cacheRead;
        var totalInput = inputTokens + cachedTokens;
        return new Dictionary<string, object?>
        {
            ["input_tokens"] = totalInput,
            ["output_tokens"] = outputTokens,
            ["total_tokens"] = totalInput + outputTokens,
            ["input_tokens_details"] = new Dictionary<string, object?>
            {
                ["cached_tokens"] = cachedTokens
            }
        };
    }

    private static Dictionary<string, object?> ParseJsonObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? (Dictionary<string, object?>)FromJsonElement(document.RootElement)!
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

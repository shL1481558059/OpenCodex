using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    public static async IAsyncEnumerable<SseEvent> ParseEvents(
        IAsyncEnumerable<string> lines,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var eventName = "message";
        var dataLines = new List<string>();
        await foreach (var rawLine in lines.WithCancellation(cancellationToken))
        {
            var line = rawLine.TrimEnd('\r', '\n');
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return new SseEvent(eventName, ParseData(string.Join("\n", dataLines)));
                }

                eventName = "message";
                dataLines = [];
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(line["data:".Length..].TrimStart());
            }
        }

        if (dataLines.Count > 0)
        {
            yield return new SseEvent(eventName, ParseData(string.Join("\n", dataLines)));
        }
    }

    public static bool CountsForTtft(string line)
    {
        return line.Contains("response.output_text.delta", StringComparison.Ordinal)
            || line.Contains("response.reasoning_summary_text.delta", StringComparison.Ordinal)
            || line.Contains("response.function_call_arguments.delta", StringComparison.Ordinal)
            || line.Contains("response.custom_tool_call_input.delta", StringComparison.Ordinal)
            || line.Contains("response.output_item.done", StringComparison.Ordinal);
    }

    private static object? ParseData(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return FromJsonElement(document.RootElement);
        }
        catch (JsonException)
        {
            return data;
        }
    }

    private static Dictionary<string, object?> ChatUsageToResponsesUsage(
        IReadOnlyDictionary<string, object?> usage)
    {
        var inputTokens = ToInt(GetValue(usage, "prompt_tokens"));
        var outputTokens = ToInt(GetValue(usage, "completion_tokens"));
        var responseUsage = new Dictionary<string, object?>
        {
            ["input_tokens"] = inputTokens,
            ["output_tokens"] = outputTokens,
            ["total_tokens"] = inputTokens + outputTokens
        };

        if (TryAsObject(GetValue(usage, "prompt_tokens_details"), out var promptDetails)
            && promptDetails.Count > 0)
        {
            responseUsage["input_tokens_details"] = new Dictionary<string, object?>
            {
                ["cached_tokens"] = ToInt(GetValue(promptDetails, "cached_tokens"))
            };
        }
        else if (TryAsObject(GetValue(usage, "input_tokens_details"), out var inputDetails)
                 && inputDetails.Count > 0)
        {
            responseUsage["input_tokens_details"] = inputDetails;
        }

        if (TryAsObject(GetValue(usage, "completion_tokens_details"), out var completionDetails)
            && completionDetails.Count > 0)
        {
            var outputDetails = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (GetValue(completionDetails, "reasoning_tokens") is not null)
            {
                outputDetails["reasoning_tokens"] = ToInt(GetValue(completionDetails, "reasoning_tokens"));
            }

            if (outputDetails.Count > 0)
            {
                responseUsage["output_tokens_details"] = outputDetails;
            }
        }

        return responseUsage;
    }

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
